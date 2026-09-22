using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ebs50_backend.Data;
using ebs50_backend.DTOs;
using ebs50_backend.Models;
using ebs50_backend.Services;
using ebs50_backend.Services.Database;
using ebs50_backend.Services.Dispatching;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;

namespace ebs50_backend.Tests;

public sealed class DatabaseMaintenanceTests
{
    [Fact]
    public async Task ExportRestoreAndResume_PreservesAllTables_ThenReplacesHistoricalJobs()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        var backup = await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default));
        Assert.Equal("Succeeded", backup.Status);
        Assert.Equal(5, backup.Preview!.TableCounts.Count);
        Assert.All(backup.Preview.TableCounts.Values, count => Assert.Equal(1, count));

        await using (var db = fixture.OpenDb())
        {
            (await db.ModelItems.SingleAsync()).Description = "newer data";
            await db.SaveChangesAsync();
        }
        var import = await fixture.UploadAsync(backup.Id);
        Assert.Equal("Succeeded", import.Status);
        using var requestToken = new CancellationTokenSource();
        var queued = await fixture.Service.QueueRestoreAsync(import.Id, requestToken.Token);
        requestToken.Cancel(); // Accepted work must outlive its HTTP caller.
        var restored = await fixture.CompleteAsync(queued);
        Assert.Equal("Succeeded", restored.Status);
        Assert.True(File.Exists(restored.RecoveryBackup));
        Assert.True(fixture.Coordinator.SyncPaused);
        Assert.Null(fixture.Coordinator.TryEnter(worker: true));
        await using (var db = fixture.OpenDb())
        {
            Assert.Equal("backup data", (await db.ModelItems.SingleAsync()).Description);
            Assert.Equal("Dispatching", (await db.DispatchJobs.SingleAsync()).Status);
            Assert.Equal("test-password", (await db.SystemSettings.SingleAsync()).Value);
        }
        using (var before = fixture.Files.Open(restored.RecoveryBackup!))
        using (var command = before.CreateCommand())
        {
            command.CommandText = "SELECT Description FROM ModelItems;";
            Assert.Equal("newer data", command.ExecuteScalar());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.QueueRestoreAsync(import.Id, default));
        var resumed = await fixture.CompleteAsync(await fixture.Service.QueueResumeAsync(default));
        Assert.Equal("Succeeded", resumed.Status);
        Assert.False(fixture.Coordinator.SyncPaused);
        await using (var db = fixture.OpenDb())
        {
            var tag = await db.EslTags.SingleAsync();
            Assert.Equal(4, tag.DesiredRevision);
            Assert.Equal(0, tag.ConfirmedRevision);
            Assert.Null(tag.LastConfirmedAt);
            Assert.Equal(1, await db.DispatchJobs.CountAsync(j => j.Status == "Superseded"));
            Assert.Equal(1, await db.DispatchJobs.CountAsync(j => j.Status == "Pending" && j.DesiredRevision == 4));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.QueueResumeAsync(default));
    }

    [Fact]
    public async Task Restore_WaitsForExistingScope_RejectsNewWork_AndTimesOutWithoutChangingData()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        var backup = await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default));
        var import = await fixture.UploadAsync(backup.Id);
        using var inFlight = fixture.Coordinator.TryEnter(worker: true);
        var operation = await fixture.Service.QueueRestoreAsync(import.Id, default);
        await DatabaseFixture.UntilAsync(() => fixture.Coordinator.Unavailable);
        Assert.Null(fixture.Coordinator.TryEnter());
        Assert.Null(fixture.Coordinator.TryEnter(worker: true));
        await Assert.ThrowsAsync<DatabaseMaintenanceBusyException>(() => fixture.Service.QueueExportAsync(default));
        var result = await fixture.CompleteAsync(operation);
        Assert.Equal("Failed", result.Status);
        Assert.Contains("chưa thay", result.Error);
        Assert.False(fixture.Coordinator.Unavailable);
        Assert.False(fixture.Coordinator.SyncPaused);
        Assert.False(File.Exists(fixture.Service.JournalPath));
        await using var db = fixture.OpenDb();
        Assert.Equal("backup data", (await db.ModelItems.SingleAsync()).Description);
    }

    [Fact]
    public async Task ValidatedFileTamperedBeforeRestore_IsRejectedBeforeBackupOrReplacement()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        var backup = await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default));
        var import = await fixture.UploadAsync(backup.Id);
        await File.AppendAllTextAsync(Path.Combine(fixture.Location.MaintenancePath, import.Id.ToString("N"), "database.db"), "tampered");
        var result = await fixture.CompleteAsync(await fixture.Service.QueueRestoreAsync(import.Id, default));
        Assert.Equal("Failed", result.Status);
        Assert.Contains("Checksum", result.Error);
        Assert.Null(result.RecoveryBackup);
        Assert.False(fixture.Coordinator.Unavailable);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("schema")]
    [InlineData("foreign-key")]
    [InlineData("count")]
    [InlineData("version")]
    public async Task InvalidBackup_IsRejectedWithoutChangingLiveDatabase(string corruption)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        var archiveFolder = Path.Combine(fixture.Root, "archive");
        Directory.CreateDirectory(archiveFolder);
        var manifest = await fixture.Files.CreateArchiveAsync(archiveFolder, default);
        var database = Path.Combine(archiveFolder, "database.db");
        if (corruption is "schema" or "foreign-key")
        {
            using var connection = fixture.Files.Open(database, false);
            using var command = connection.CreateCommand();
            command.CommandText = corruption == "schema" ? "CREATE TABLE Extra (Id INTEGER);"
                : "PRAGMA foreign_keys=OFF; UPDATE EslTags SET ModelCode='missing';";
            command.ExecuteNonQuery();
            connection.Close();
            manifest = manifest with { DatabaseSha256 = await DatabaseBackupFiles.HashAsync(database, default) };
        }
        if (corruption == "checksum") manifest = manifest with { DatabaseSha256 = "invalid" };
        if (corruption == "count") manifest.TableCounts["EslTags"] = 42;
        if (corruption == "version") manifest = manifest with { FormatVersion = 99 };
        var upload = Path.Combine(fixture.Root, "invalid.zip");
        using (var zip = ZipFile.Open(upload, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(database, "database.db");
            using var entry = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(entry, manifest, DatabaseBackupFiles.JsonOptions);
        }
        await using var stream = File.OpenRead(upload);
        var result = await fixture.CompleteAsync(await fixture.Service.QueueValidationAsync(stream, default));
        Assert.Equal("Failed", result.Status);
        Assert.False(fixture.Coordinator.Unavailable);
        await using var db = fixture.OpenDb();
        Assert.Equal("backup data", (await db.ModelItems.SingleAsync()).Description);
    }

    [Theory]
    [InlineData("../database.db")]
    [InlineData("database.db")]
    public async Task ArchiveWithTraversalOrDuplicateEntry_IsRejected(string extraEntry)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var upload = new MemoryStream();
        using (var archive = new ZipArchive(upload, ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("database.db");
            archive.CreateEntry("manifest.json");
            archive.CreateEntry(extraEntry);
        }
        upload.Position = 0;
        var result = await fixture.CompleteAsync(await fixture.Service.QueueValidationAsync(upload, default));
        Assert.Equal("Failed", result.Status);
        Assert.Contains("ZIP", result.Error);
    }

    [Fact]
    public async Task BoundedCopy_RejectsOversizedOrCanceledInput()
    {
        using var source = new MemoryStream(new byte[16]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseBackupFiles.CopyBoundedAsync(source, destination, 8, default));
        source.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseBackupFiles.CopyBoundedAsync(source, destination, 100, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StartupRecovery_RestoresOldSnapshotEvenIfLiveDatabaseIsMissingOrCorrupt(bool replaced, bool missing)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        await fixture.StopAsync();
        var id = Guid.NewGuid();
        var directory = Path.Combine(fixture.Location.MaintenancePath, id.ToString("N"));
        Directory.CreateDirectory(directory);
        fixture.Files.Snapshot(Path.Combine(directory, "before.db"));
        DatabaseBackupFiles.WriteDurable(fixture.Service.JournalPath, new DatabaseRecoveryJournal(id, false, false));
        if (replaced)
        {
            fixture.Files.PrepareReplacement(discardCurrent: true);
            if (missing) File.Delete(fixture.Location.DatabasePath);
            else await File.WriteAllTextAsync(fixture.Location.DatabasePath, "interrupted file replacement");
            DatabaseBackupFiles.WriteDurable(fixture.Service.PausePath, new { id });
            DatabaseBackupFiles.WriteDurable(fixture.Service.RestoredPath, new { id });
        }
        await fixture.Service.RecoverOnStartupAsync(default);
        Assert.False(File.Exists(fixture.Service.JournalPath));
        Assert.False(fixture.Coordinator.SyncPaused);
        Assert.False(fixture.Service.HasRestoredDatabase);
        await using var db = fixture.OpenDb();
        Assert.Equal("backup data", (await db.ModelItems.SingleAsync()).Description);
    }

    [Fact]
    public async Task SuccessfulRestore_RemainsPausedOnStartup()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var backup = await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default));
        var import = await fixture.UploadAsync(backup.Id);
        Assert.Equal("Succeeded", (await fixture.CompleteAsync(await fixture.Service.QueueRestoreAsync(import.Id, default))).Status);
        await fixture.StopAsync();
        fixture.Coordinator.SetSyncPaused(false);
        await fixture.Service.RecoverOnStartupAsync(default);
        Assert.True(fixture.Coordinator.SyncPaused);
        Assert.True(fixture.Service.HasRestoredDatabase);
        await using var db = fixture.OpenDb();
        var initializer = new DbInitializer(db, NullLogger<DbInitializer>.Instance, new TestEnvironment(fixture.Root), fixture.Service);
        await initializer.InitializeAsync();
        Assert.Empty(await db.EslTags.ToListAsync());
        Assert.Empty(await db.MachineStates.ToListAsync());
        Assert.Empty(await db.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task ExportDuringConcurrentWrites_ProducesAnInternallyConsistentSnapshot()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        using var stop = new CancellationTokenSource();
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await using var db = fixture.OpenDb();
            int index = 0;
            while (!stop.IsCancellationRequested)
            {
                db.ModelItems.Add(new ModelItem { ModelCode = $"M{index++}" });
                await db.SaveChangesAsync();
                firstWrite.TrySetResult();
                await Task.Delay(1);
            }
        });
        try
        {
            await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var export = await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default));
            Assert.Equal("Succeeded", export.Status);
            Assert.True(export.Preview!.TableCounts["ModelItems"] >= 2);
            Assert.Equal("Succeeded", (await fixture.UploadAsync(export.Id)).Status);
        }
        finally { stop.Cancel(); await writer; }
    }

    [Fact]
    public async Task CandidateWriteFailure_LeavesCurrentDatabaseAvailable()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        var backup = await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default));
        var import = await fixture.UploadAsync(backup.Id);
        using var lockedCandidate = new FileStream(fixture.Location.DatabasePath + ".restore-candidate", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var result = await fixture.CompleteAsync(await fixture.Service.QueueRestoreAsync(import.Id, default));
        Assert.Equal("Failed", result.Status);
        Assert.True(File.Exists(result.RecoveryBackup));
        Assert.False(fixture.Coordinator.Unavailable);
        Assert.False(fixture.Coordinator.SyncPaused);
        Assert.False(File.Exists(fixture.Service.JournalPath));
        await using var db = fixture.OpenDb();
        Assert.Equal("backup data", (await db.ModelItems.SingleAsync()).Description);
    }

    [Fact]
    public async Task ExpiredUpload_CannotBeRestored_AndStartupRemovesTemporaryArchive()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var backup = await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default));
        var import = await fixture.UploadAsync(backup.Id);
        await fixture.StopAsync();
        var folder = Path.Combine(fixture.Location.MaintenancePath, import.Id.ToString("N"));
        DatabaseBackupFiles.WriteDurable(Path.Combine(folder, "operation.json"), import with { ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.QueueRestoreAsync(import.Id, default));
        await fixture.Service.RecoverOnStartupAsync(default);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task CorruptArchiveAndCanceledUpload_DoNotBlockNextOperation()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var input = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.QueueValidationAsync(input, new CancellationToken(true)));
        var result = await fixture.CompleteAsync(await fixture.Service.QueueValidationAsync(input, default));
        Assert.Equal("Failed", result.Status);
        Assert.Equal("Succeeded", (await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default))).Status);
    }

    [Fact]
    public async Task LockedDatabase_RestoreRollsBackOrStaysClosed_WithRecoveryImageIntact()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        var backup = await fixture.CompleteAsync(await fixture.Service.QueueExportAsync(default));
        var import = await fixture.UploadAsync(backup.Id);
        using (var externalReader = new FileStream(fixture.Location.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var result = await fixture.CompleteAsync(await fixture.Service.QueueRestoreAsync(import.Id, default));
            Assert.Equal("Failed", result.Status);
            Assert.True(File.Exists(result.RecoveryBackup));
            // Windows refuses replacement while this external handle does not share deletion.
            Assert.True(fixture.Coordinator.Unavailable);
            Assert.True(File.Exists(fixture.Service.JournalPath));
        }
        await fixture.StopAsync();
        await fixture.Service.RecoverOnStartupAsync(default);
        await using var db = fixture.OpenDb();
        Assert.Equal("backup data", (await db.ModelItems.SingleAsync()).Description);
    }

    [Fact]
    public async Task Coordinator_CancellationReleasesGate_AndFaultKeepsItClosed()
    {
        var coordinator = new DatabaseMaintenanceCoordinator();
        using var active = coordinator.TryEnter();
        using var token = new CancellationTokenSource();
        var exclusive = coordinator.EnterMaintenanceAsync(TimeSpan.FromSeconds(10), token.Token);
        Assert.Null(coordinator.TryEnter());
        token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exclusive);
        using var afterCancel = coordinator.TryEnter();
        Assert.NotNull(afterCancel);
        coordinator.MarkFaulted();
        Assert.Null(coordinator.TryEnter());
    }

    [Theory]
    [InlineData("192.168.1.2", "localhost", false, 403)]
    [InlineData("127.0.0.1", "attacker.example", false, 403)]
    [InlineData("127.0.0.1", "localhost", true, 403)]
    [InlineData("127.0.0.1", "localhost", false, 200)]
    [InlineData("::1", "localhost", false, 200)]
    public async Task MaintenanceAccess_RequiresDirectLoopback(string remote, string host, bool forwarded, int expected)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/api/database/status";
        context.Request.Host = new HostString(host);
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (forwarded) context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        var middleware = new DatabaseMaintenanceMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, new DatabaseMaintenanceCoordinator(), services.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(expected, context.Response.StatusCode);
    }

    [Fact]
    public async Task RequestScope_IsDisposedBeforeExclusiveAccessIsGranted()
    {
        var disposed = false;
        using var services = new ServiceCollection().AddScoped(_ => new DisposalProbe(() => disposed = true)).BuildServiceProvider();
        var coordinator = new DatabaseMaintenanceCoordinator();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/tags";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new DatabaseMaintenanceMiddleware(async request =>
        {
            _ = request.RequestServices.GetRequiredService<DisposalProbe>();
            entered.SetResult();
            await finish.Task;
        });
        var requestTask = middleware.InvokeAsync(context, coordinator, services.GetRequiredService<IServiceScopeFactory>());
        await entered.Task;
        var drain = coordinator.EnterMaintenanceAsync(TimeSpan.FromSeconds(5), default);
        Assert.False(drain.IsCompleted);
        finish.SetResult();
        using var exclusive = await drain;
        Assert.True(disposed);
        await requestTask;
        Assert.Same(services, context.RequestServices);
    }

    private sealed class DisposalProbe(Action onDispose) : IDisposable { public void Dispose() => onDispose(); }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

internal sealed class DatabaseFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ebs50-database-tests", Guid.NewGuid().ToString("N"));
    public DatabaseLocation Location { get; }
    public DatabaseBackupFiles Files { get; }
    public DatabaseMaintenanceCoordinator Coordinator { get; } = new();
    public DatabaseMaintenanceService Service { get; }
    private readonly ServiceProvider _provider;
    private bool _stopped;

    private DatabaseFixture()
    {
        Directory.CreateDirectory(Root);
        Location = new DatabaseLocation(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "live.db"), Pooling = false }.ToString());
        Files = new DatabaseBackupFiles(Location);
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(Location.ConnectionString));
        services.AddSingleton<IEslDispatchQueue, TestQueue>();
        _provider = services.BuildServiceProvider();
        Service = new DatabaseMaintenanceService(Location, Files, Coordinator, _provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DatabaseMaintenance:DrainTimeoutSeconds"] = "1" }).Build(),
            NullLogger<DatabaseMaintenanceService>.Instance);
    }

    public static async Task<DatabaseFixture> CreateAsync()
    {
        var fixture = new DatabaseFixture();
        await using var db = fixture.OpenDb();
        await db.Database.EnsureCreatedAsync();
        await fixture.Service.RecoverOnStartupAsync(default);
        await fixture.Service.StartAsync(default);
        return fixture;
    }

    public AppDbContext OpenDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Location.ConnectionString).Options);

    public async Task SeedAsync()
    {
        await using var db = OpenDb();
        db.MachineStates.Add(new MachineState { StateCode = 0, StateNameVi = "Running" });
        db.ModelItems.Add(new ModelItem { ModelCode = "MODEL", Description = "backup data" });
        db.EslTags.Add(new EslTag { MacAddress = "B4B819DE", MachineNo = "M1", ModelCode = "MODEL", CurrentStateCode = 0,
            DesiredRevision = 3, ConfirmedRevision = 2, LastConfirmedAt = DateTime.UtcNow, SyncStatus = "Dispatching" });
        db.DispatchJobs.Add(new DispatchJob { MacAddress = "B4B819DE", MachineNo = "M1", ModelCode = "MODEL", StateCode = 0,
            DesiredRevision = 3, BindingVersion = 1, Status = "Dispatching" });
        db.SystemSettings.Add(new SystemSetting { Key = "Ebs50Password", Value = "test-password" });
        await db.SaveChangesAsync();
    }

    public async Task<DatabaseOperation> CompleteAsync(DatabaseOperation operation)
    {
        await UntilAsync(() => Service.GetOperation(operation.Id)?.Status is "Succeeded" or "Failed");
        return Service.GetOperation(operation.Id)!;
    }

    public async Task<DatabaseOperation> UploadAsync(Guid exportId)
    {
        var download = Service.OpenDownload(exportId)!.Value;
        await using var input = download.Stream;
        return await CompleteAsync(await Service.QueueValidationAsync(input, default));
    }

    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        await Service.StopAsync(default);
        _stopped = true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Service.Dispose();
        await _provider.DisposeAsync();
        Directory.Delete(Root, true);
    }

    private sealed class TestQueue : IEslDispatchQueue
    {
        public int PendingCount => 0;
        public ValueTask EnqueueAsync(EslDispatchJob job, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<EslDispatchJob> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public void NotifyJobAvailable() { }
        public Task<bool> WaitForJobAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
