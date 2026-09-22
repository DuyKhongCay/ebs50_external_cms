using System.Threading.Channels;
using ebs50_backend.Data;
using ebs50_backend.DTOs;
using ebs50_backend.Models;
using ebs50_backend.Services.Dispatching;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Services.Database;

public sealed class DatabaseMaintenanceBusyException() : Exception("Đang có tác vụ bảo trì database khác.");

public sealed record DatabaseRecoveryJournal(Guid OperationId, bool WasPaused, bool WasRestored);

/// <summary>Serial maintenance jobs with durable recovery metadata outside the database being replaced.</summary>
public sealed class DatabaseMaintenanceService : BackgroundService
{
    private readonly DatabaseLocation _location;
    private readonly DatabaseBackupFiles _files;
    private readonly DatabaseMaintenanceCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DatabaseMaintenanceService> _logger;
    private readonly Channel<(Guid Id, Guid? ImportId)> _jobs = Channel.CreateBounded<(Guid, Guid?)>(1);
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly object _stateLock = new();
    private readonly FileStream _processLock;
    private readonly TimeSpan _drainTimeout;
    public string JournalPath => Path.Combine(_location.MaintenancePath, "restore.json");
    public string PausePath => Path.Combine(_location.MaintenancePath, "sync-paused.json");
    public string RestoredPath => Path.Combine(_location.MaintenancePath, "restored.json");
    public bool HasRestoredDatabase => File.Exists(RestoredPath);

    public DatabaseMaintenanceService(DatabaseLocation location, DatabaseBackupFiles files,
        DatabaseMaintenanceCoordinator coordinator, IServiceScopeFactory scopes,
        IConfiguration configuration, ILogger<DatabaseMaintenanceService> logger)
    {
        _location = location;
        _files = files;
        _coordinator = coordinator;
        _scopes = scopes;
        _logger = logger;
        _drainTimeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int>("DatabaseMaintenance:DrainTimeoutSeconds", 30), 1, 300));
        Directory.CreateDirectory(location.MaintenancePath);
        // Prevent two instances of this application from restoring the same database.
        _processLock = new FileStream(Path.Combine(location.MaintenancePath, "process.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string Folder(Guid id) => Path.Combine(_location.MaintenancePath, id.ToString("N"));
    private string OperationPath(Guid id) => Path.Combine(Folder(id), "operation.json");

    public DatabaseOperation? GetOperation(Guid id)
    {
        lock (_stateLock)
        {
            if (!File.Exists(OperationPath(id))) return null;
            var operation = DatabaseBackupFiles.ReadJson<DatabaseOperation>(OperationPath(id));
            return operation.ExpiresAtUtc > DateTime.UtcNow || File.Exists(Path.Combine(Folder(id), "before.db")) ? operation : null;
        }
    }

    private void Save(DatabaseOperation operation)
    {
        lock (_stateLock) DatabaseBackupFiles.WriteDurable(OperationPath(operation.Id), operation);
    }

    private DatabaseOperation Create(string kind)
    {
        var operation = new DatabaseOperation(Guid.NewGuid(), kind, "Queued", "Đang chờ xử lý", DateTime.UtcNow,
            DateTime.UtcNow.AddHours(kind == "Validate" ? 1 : 24));
        Directory.CreateDirectory(Folder(operation.Id));
        Save(operation);
        return operation;
    }

    private async Task ReserveAsync(CancellationToken cancellationToken)
    {
        if (_coordinator.Unavailable || !await _admission.WaitAsync(0, cancellationToken))
            throw new DatabaseMaintenanceBusyException();
    }

    public async Task<DatabaseOperation> QueueExportAsync(CancellationToken cancellationToken)
    {
        await ReserveAsync(cancellationToken);
        try
        {
            var operation = Create("Export");
            Queue(operation.Id);
            return operation;
        }
        catch { _admission.Release(); throw; }
    }

    public async Task<DatabaseOperation> QueueValidationAsync(Stream upload, CancellationToken cancellationToken)
    {
        await ReserveAsync(cancellationToken);
        DatabaseOperation? operation = null;
        try
        {
            operation = Create("Validate");
            await using (var output = new FileStream(Path.Combine(Folder(operation.Id), "upload.zip"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, true))
                await DatabaseBackupFiles.CopyBoundedAsync(upload, output, DatabaseBackupFiles.MaxUploadBytes, cancellationToken);
            Queue(operation.Id);
            return operation;
        }
        catch
        {
            try { if (operation != null) Save(operation with { Status = "Failed", Stage = "Upload chưa hoàn tất" }); }
            finally { _admission.Release(); }
            throw;
        }
    }

    public async Task<DatabaseOperation> QueueRestoreAsync(Guid importId, CancellationToken cancellationToken)
    {
        await ReserveAsync(cancellationToken);
        try
        {
            var import = GetOperation(importId);
            if (import is not { Kind: "Validate", Status: "Succeeded", Preview: not null })
                throw new InvalidDataException("File import chưa được kiểm tra, đã dùng hoặc đã hết hạn.");
            var operation = Create("Restore");
            Save(import with { Status = "Consumed", Stage = "Đã xác nhận khôi phục" });
            Queue(operation.Id, importId);
            return operation;
        }
        catch { _admission.Release(); throw; }
    }

    public async Task<DatabaseOperation> QueueResumeAsync(CancellationToken cancellationToken)
    {
        await ReserveAsync(cancellationToken);
        try
        {
            if (!_coordinator.SyncPaused) throw new InvalidDataException("Không có lần khôi phục đang chờ đồng bộ lại.");
            var operation = Create("Resume");
            Queue(operation.Id);
            return operation;
        }
        catch { _admission.Release(); throw; }
    }

    private void Queue(Guid id, Guid? importId = null)
    {
        if (!_jobs.Writer.TryWrite((id, importId))) throw new DatabaseMaintenanceBusyException();
    }

    public (Stream Stream, string Name)? OpenDownload(Guid id)
    {
        lock (_stateLock)
        {
            var operation = GetOperation(id);
            if (operation is not { Kind: "Export", Status: "Succeeded" }) return null;
            // Do not share deletion: cleanup will skip the directory while a download is active on Windows.
            return (new FileStream(Path.Combine(Folder(id), "backup.zip"), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true),
                $"ebs50-backup-{operation.CreatedAtUtc:yyyyMMdd-HHmmss}.zip");
        }
    }

    /// <summary>Must run before DbInitializer, hosted workers, or web requests can access SQLite.</summary>
    public async Task RecoverOnStartupAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(JournalPath))
        {
            var journal = DatabaseBackupFiles.ReadJson<DatabaseRecoveryJournal>(JournalPath);
            _logger.LogWarning("Recovering interrupted database restore {OperationId}", journal.OperationId);
            await RollbackAsync(journal, cancellationToken);
        }
        _coordinator.SetSyncPaused(File.Exists(PausePath));
        CleanupCandidate();
        foreach (var directory in OperationDirectories())
        {
            var path = Path.Combine(directory, "operation.json");
            if (!File.Exists(path)) continue;
            var operation = DatabaseBackupFiles.ReadJson<DatabaseOperation>(path);
            if (operation.Status is "Queued" or "Running")
                Save(operation with { Status = "Failed", Stage = "Server đã khởi động lại",
                    Error = "Tác vụ bị gián đoạn. Kiểm tra trạng thái database và đồng bộ trước khi tiếp tục." });
        }
        CleanupExpired();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // SQLite backup is synchronous; never execute it inline during a controller call or host startup.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                tick.CancelAfter(TimeSpan.FromMinutes(1));
                (Guid Id, Guid? ImportId) job;
                try { job = await _jobs.Reader.ReadAsync(tick.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    CleanupExpired();
                    continue;
                }
                var operation = GetOperation(job.Id)!;
                try
                {
                    Save(operation with { Status = "Running", Stage = "Đang xử lý" });
                    switch (operation.Kind)
                    {
                        case "Export":
                            using (var lease = _coordinator.TryEnter() ?? throw new DatabaseMaintenanceBusyException())
                                operation = operation with { Preview = await _files.CreateArchiveAsync(Folder(job.Id), stoppingToken) };
                            break;
                        case "Validate":
                            using (var lease = _coordinator.TryEnter() ?? throw new DatabaseMaintenanceBusyException())
                                operation = operation with { Preview = await _files.ExtractAndValidateAsync(Folder(job.Id), stoppingToken) };
                            break;
                        case "Restore":
                            operation = await RestoreAsync(operation, job.ImportId!.Value, stoppingToken);
                            break;
                        case "Resume":
                            await ResumeAsync(stoppingToken);
                            break;
                    }
                    Save(operation with { Status = "Succeeded", Stage = operation.Kind == "Restore" ? "Đã khôi phục; đang tạm dừng đồng bộ EBS" : "Hoàn tất" });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Database maintenance {OperationId} ({Kind}) failed", operation.Id, operation.Kind);
                    // Preserve the recovery path written by RestoreAsync even when it throws.
                    operation = GetOperation(operation.Id) ?? operation;
                    Save(operation with { Status = "Failed", Stage = "Không hoàn tất",
                        Error = ex is InvalidDataException ? ex.Message : ex is TimeoutException
                            ? "Hết thời gian chờ truy cập database kết thúc; chưa thay dữ liệu."
                            : "Tác vụ thất bại. Xem log server và trạng thái bảo trì trước khi thử lại." });
                }
                finally
                {
                    if (operation.Kind == "Restore") CleanupCandidate();
                    _admission.Release();
                }
                CleanupExpired();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<DatabaseOperation> RestoreAsync(DatabaseOperation operation, Guid importId, CancellationToken cancellationToken)
    {
        var import = GetOperation(importId) ?? throw new InvalidDataException("File import đã hết hạn.");
        var importedPath = Path.Combine(Folder(importId), "database.db");
        await _files.ValidateAsync(importedPath, import.Preview!, cancellationToken);
        Save(operation with { Status = "Running", Stage = "Chờ request và worker kết thúc" });
        using var exclusive = await _coordinator.EnterMaintenanceAsync(_drainTimeout, cancellationToken);
        var schema = _files.ReadSchema(_location.DatabasePath);
        var backup = Path.Combine(Folder(operation.Id), "before.db");
        Save(operation with { Status = "Running", Stage = "Sao lưu database hiện tại" });
        _files.Snapshot(backup);
        _files.Inspect(backup, schema);
        // Flush the rollback image before committing the recovery journal.
        using (var stream = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
        operation = operation with { RecoveryBackup = backup };
        Save(operation with { Status = "Running", Stage = "Đang khôi phục" });
        var candidate = _location.DatabasePath + ".restore-candidate";
        await DatabaseBackupFiles.CopyDurableAsync(importedPath, candidate, cancellationToken);
        await _files.ValidateAsync(candidate, import.Preview!, cancellationToken);
        _files.PrepareReplacement();
        cancellationToken.ThrowIfCancellationRequested();
        var journal = new DatabaseRecoveryJournal(operation.Id, File.Exists(PausePath), HasRestoredDatabase);
        DatabaseBackupFiles.WriteDurable(JournalPath, journal);
        // From here cancellation belongs to recovery, not the HTTP request or host shutdown.
        try
        {
            DatabaseBackupFiles.WriteDurable(PausePath, new { operation.Id });
            DatabaseBackupFiles.WriteDurable(RestoredPath, new { operation.Id });
            _coordinator.SetSyncPaused(true);
            File.Replace(candidate, _location.DatabasePath, null);
            _files.Inspect(_location.DatabasePath, schema);
            _coordinator.MarkRestored();
            File.Delete(JournalPath);
            return operation;
        }
        catch
        {
            try
            {
                using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await RollbackAsync(journal, recoveryTimeout.Token);
            }
            catch (Exception recoveryError)
            {
                _coordinator.MarkFaulted();
                _logger.LogCritical(recoveryError, "Rollback failed. Database remains unavailable. Recovery backup: {Backup}", backup);
            }
            throw;
        }
    }

    private async Task RollbackAsync(DatabaseRecoveryJournal journal, CancellationToken cancellationToken)
    {
        var backup = Path.Combine(Folder(journal.OperationId), "before.db");
        var schema = _files.ReadSchema(backup);
        _files.Inspect(backup, schema);
        var candidate = _location.DatabasePath + ".restore-candidate";
        await DatabaseBackupFiles.CopyDurableAsync(backup, candidate, cancellationToken);
        _files.PrepareReplacement(discardCurrent: true);
        if (File.Exists(_location.DatabasePath)) File.Replace(candidate, _location.DatabasePath, null);
        else File.Move(candidate, _location.DatabasePath);
        _files.Inspect(_location.DatabasePath, schema);
        if (journal.WasPaused) DatabaseBackupFiles.WriteDurable(PausePath, new { journal.OperationId });
        else File.Delete(PausePath);
        if (journal.WasRestored) DatabaseBackupFiles.WriteDurable(RestoredPath, new { journal.OperationId });
        else File.Delete(RestoredPath);
        _coordinator.SetSyncPaused(journal.WasPaused);
        _coordinator.MarkRestored();
        File.Delete(JournalPath);
    }

    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        using var exclusive = await _coordinator.EnterMaintenanceAsync(_drainTimeout, cancellationToken);
        if (!_coordinator.SyncPaused) throw new InvalidDataException("Đồng bộ đã được tiếp tục.");
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var stale = await db.DispatchJobs.Where(j => j.Status == "Pending" || j.Status == "Dispatching" || j.Status == "Uploaded").ToListAsync(cancellationToken);
            foreach (var job in stale) job.Status = "Superseded";
            var tags = await db.EslTags.ToListAsync(cancellationToken);
            foreach (var tag in tags)
            {
                tag.DesiredRevision = checked(tag.DesiredRevision + 1);
                tag.ConfirmedRevision = 0;
                tag.SyncStatus = "Pending";
                tag.LastSyncedAt = tag.LastUploadedAt = tag.LastConfirmedAt = null;
                db.DispatchJobs.Add(new DispatchJob
                {
                    MacAddress = tag.MacAddress, MachineNo = tag.MachineNo, ModelCode = tag.ModelCode,
                    StateCode = tag.CurrentStateCode, DesiredRevision = tag.DesiredRevision, BindingVersion = tag.BindingVersion,
                    Status = "Pending", CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // If the process stops between commit and this deletion, workers remain paused. A retry supersedes those jobs.
        File.Delete(PausePath);
        _coordinator.MarkRestored();
        _coordinator.SetSyncPaused(false);
        using var queueScope = _scopes.CreateScope();
        queueScope.ServiceProvider.GetRequiredService<IEslDispatchQueue>().NotifyJobAvailable();
    }

    private IEnumerable<string> OperationDirectories() => Directory.EnumerateDirectories(_location.MaintenancePath)
        .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);

    private void CleanupCandidate()
    {
        if (File.Exists(JournalPath)) return;
        try { File.Delete(_location.DatabasePath + ".restore-candidate"); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not remove the unused restore candidate"); }
        catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "Could not remove the unused restore candidate"); }
    }

    private void CleanupExpired()
    {
        try
        {
            lock (_stateLock)
            {
                foreach (var directory in OperationDirectories())
                {
                    // Recovery images are retained for the operator; never expire or automatically delete them.
                    if (File.Exists(Path.Combine(directory, "before.db"))) continue;
                    var path = Path.Combine(directory, "operation.json");
                    if (!File.Exists(path)) continue;
                    var operation = DatabaseBackupFiles.ReadJson<DatabaseOperation>(path);
                    if (operation.ExpiresAtUtc > DateTime.UtcNow) continue;
                    try
                    {
                        // Flat, server-created directories only. Delete archive first so active downloads retain metadata.
                        var archive = Path.Combine(directory, "backup.zip");
                        File.Delete(archive);
                        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                        Directory.Delete(directory);
                    }
                    catch (IOException) { /* An active download or another temporary file lock: retry next minute. */ }
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not clean up expired database maintenance files"); }
    }

    public override void Dispose()
    {
        base.Dispose();
        _processLock.Dispose();
        _admission.Dispose();
    }
}
