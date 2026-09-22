using System.Collections.Concurrent;
using ebs50_backend.Data;
using ebs50_backend.Models;
using ebs50_backend.Services.Rendering;
using Microsoft.EntityFrameworkCore;
using ebs50_backend.Services.Database;

namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Background worker implementing the Transactional Outbox pattern with per-MAC non-blocking scheduling.
/// Polls persistent DispatchJobs from SQLite, coordinates image rendering, generates XML manifests,
/// and transfers payloads to EBS-50 via native async SFTP.
/// Eliminates Head-of-Line blocking: a MAC in cooldown never blocks dispatches to other MACs.
/// </summary>
internal sealed class EslDispatcherBackgroundWorker(
    IEslDispatchQueue queue,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    DatabaseMaintenanceCoordinator maintenance,
    ILogger<EslDispatcherBackgroundWorker> logger) : BackgroundService
{
    // Tracks the last successful dispatch timestamp per MAC to enforce cooldown
    private readonly ConcurrentDictionary<string, DateTime> _lastDispatchTimes = new(StringComparer.OrdinalIgnoreCase);

    // Tracks currently in-flight MAC dispatches to guarantee strict per-MAC sequential delivery
    private readonly ConcurrentDictionary<string, byte> _activeMacs = new(StringComparer.OrdinalIgnoreCase);
    private long _databaseGeneration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EslDispatcherBackgroundWorker started and listening for persistent dispatch jobs.");

        // Startup recovery: Reset any jobs left in 'Dispatching' state from a previous unexpected shutdown
        await RecoverStaleInFlightJobsAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool processedAny = await DispatchNextDueJobAsync(stoppingToken);

                if (!processedAny)
                {
                    // No jobs ready to process; wait for wake-up notification or check again in 1 second
                    await queue.WaitForJobAvailableAsync(TimeSpan.FromSeconds(1), stoppingToken);
                }
                else
                {
                    // PERF: 50ms pacing prevents tight CPU spinning while yielding to other thread pool workers
                    await Task.Delay(50, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("EslDispatcherBackgroundWorker graceful shutdown requested.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected fatal error in EslDispatcherBackgroundWorker.");
        }
        finally
        {
            await HandleGracefulShutdownAsync();
        }
    }

    private async Task<bool> DispatchNextDueJobAsync(CancellationToken stoppingToken)
    {
        using var lease = maintenance.TryEnter(worker: true);
        if (lease == null) return false;
        if (_databaseGeneration != maintenance.Generation)
        {
            _lastDispatchTimes.Clear();
            _activeMacs.Clear();
            _databaseGeneration = maintenance.Generation;
        }
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;

        // Fetch the oldest due pending job whose MAC is not currently in-flight
        var candidateJobs = await db.DispatchJobs
            .Where(j => j.Status == "Pending" && j.NextAttemptAt <= now)
            .OrderBy(j => j.CreatedAt)
            .Take(10)
            .ToListAsync(stoppingToken);

        if (candidateJobs.Count == 0)
        {
            return false;
        }

        // Find the first job whose MAC is not currently active
        DispatchJob? targetJob = null;
        foreach (var candidate in candidateJobs)
        {
            if (_activeMacs.TryAdd(candidate.MacAddress, 0))
            {
                targetJob = candidate;
                break;
            }
        }

        if (targetJob == null)
        {
            return false;
        }

        try
        {
            // STEP 0: Check per-MAC cooldown to ensure Opticon SE420RY finishes display refresh
            var minIntervalSeconds = configuration.GetValue<int>("Ebs50Settings:MinDispatchIntervalSeconds", 20);
            if (_lastDispatchTimes.TryGetValue(targetJob.MacAddress, out var lastTime))
            {
                var elapsed = now - lastTime;
                var minInterval = TimeSpan.FromSeconds(minIntervalSeconds);
                if (elapsed < minInterval)
                {
                    var waitTime = minInterval - elapsed;
                    logger.LogInformation("Tag {Mac} was dispatched {Elapsed:F1}s ago (cooldown: {Min}s). Non-blocking reschedule in {Wait:F1}s...",
                        targetJob.MacAddress, elapsed.TotalSeconds, minIntervalSeconds, waitTime.TotalSeconds);

                    // Reschedule for later; release active lease so OTHER MACs can be processed immediately!
                    targetJob.NextAttemptAt = now.Add(waitTime);
                    await db.SaveChangesAsync(stoppingToken);
                    return true;
                }
            }

            // STEP 1: Verify tag validity & binding version
            var tag = await db.EslTags.FirstOrDefaultAsync(t => t.MacAddress == targetJob.MacAddress, stoppingToken);
            if (tag == null)
            {
                logger.LogWarning("Tag {Mac} no longer exists in database. Canceling job {JobId}.", targetJob.MacAddress, targetJob.Id);
                targetJob.Status = "Superseded";
                await db.SaveChangesAsync(stoppingToken);
                return true;
            }

            if (tag.BindingVersion != targetJob.BindingVersion)
            {
                logger.LogWarning("Tag {Mac} binding version changed ({TagVer} != {JobVer}). Superseding stale job {JobId}.",
                    targetJob.MacAddress, tag.BindingVersion, targetJob.BindingVersion, targetJob.Id);
                targetJob.Status = "Superseded";
                await db.SaveChangesAsync(stoppingToken);
                return true;
            }

            // Mark job & tag as Dispatching
            targetJob.Status = "Dispatching";
            targetJob.AttemptCount += 1;
            tag.SyncStatus = "Dispatching";
            await db.SaveChangesAsync(stoppingToken);

            // STEP 2: Execute rendering and SFTP transmission
            await ExecuteDispatchAsync(targetJob, tag, db, scope.ServiceProvider, stoppingToken);
            return true;
        }
        finally
        {
            _activeMacs.TryRemove(targetJob.MacAddress, out _);
        }
    }

    private async Task ExecuteDispatchAsync(
        DispatchJob job,
        EslTag tag,
        AppDbContext db,
        IServiceProvider services,
        CancellationToken stoppingToken)
    {
        var renderService = services.GetRequiredService<ITagRenderService>();
        var sftpService = services.GetRequiredService<IEbs50SftpService>();

        logger.LogInformation("Executing dispatch job {JobId} for tag MAC: {Mac} (Model: {Model}, State: {State}, Attempt: {Attempt})",
            job.Id, job.MacAddress, job.ModelCode, job.StateCode, job.AttemptCount);

        try
        {
            var renderReq = new TagRenderRequest(
                MacAddress: job.MacAddress,
                ModelCode: job.ModelCode,
                StateCode: job.StateCode,
                MachineNo: job.MachineNo,
                OverrideThemeColor: job.OverrideThemeColor);

            var renderResult = await renderService.RenderTagImageAsync(renderReq, stoppingToken);

            var state = await db.MachineStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.StateCode == job.StateCode, stoppingToken);

            var stateDesc = state != null ? $"[{state.StateCode}] {state.StateNameVi}" : $"State {job.StateCode}";
            var note = $"{stateDesc} | {job.ModelCode}";

            var cleanMac = job.MacAddress.Trim().ToUpperInvariant();
            var imageFileName = $"{cleanMac}.png";

            // NOTE: Generate XML with unique ID = MAC to prevent cross-model state collisions and auto-refresh fallback in Opticon firmware
            var xmlContent = EslXmlGenerator.GenerateEslImageInfoXml(cleanMac, imageFileName, uniqueId: cleanMac, note);

            await sftpService.UploadTagPayloadAsync(cleanMac, imageFileName, renderResult.ImageBytes, xmlContent, stoppingToken);

            _lastDispatchTimes[job.MacAddress] = DateTime.UtcNow;

            job.Status = "Uploaded";
            job.UploadedAt = DateTime.UtcNow;
            job.LastError = null;

            tag.SyncStatus = "Uploaded";
            tag.LastUploadedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(stoppingToken);

            logger.LogInformation("Job {JobId} successfully dispatched for tag MAC: {Mac} (Image: {Image}). SyncStatus: Uploaded.",
                job.Id, job.MacAddress, imageFileName);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // NOTE: Host is shutting down; re-queue job as Pending for immediate processing upon next restart
            job.Status = "Pending";
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Job {JobId} for tag MAC: {Mac} interrupted by host shutdown. Reset to Pending.", job.Id, job.MacAddress);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch job {JobId} for tag MAC: {Mac} (Attempt {Attempt}/3).",
                job.Id, job.MacAddress, job.AttemptCount);

            job.LastError = ex.Message;

            if (job.AttemptCount < 3)
            {
                // Exponential backoff retry: 5s, 10s
                var retryDelaySeconds = (int)Math.Pow(2, job.AttemptCount) * 5;
                job.Status = "Pending";
                job.NextAttemptAt = DateTime.UtcNow.AddSeconds(retryDelaySeconds);
                logger.LogWarning("Rescheduling job {JobId} for tag MAC: {Mac} in {Delay}s.", job.Id, job.MacAddress, retryDelaySeconds);
            }
            else
            {
                job.Status = "Failed";
                tag.SyncStatus = "Error";
                logger.LogError("Job {JobId} for tag MAC: {Mac} permanently failed after {Attempts} attempts.", job.Id, job.MacAddress, job.AttemptCount);
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task RecoverStaleInFlightJobsAsync(CancellationToken cancellationToken)
    {
        using var lease = maintenance.TryEnter(worker: true);
        if (lease == null) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var staleJobs = await db.DispatchJobs
                .Where(j => j.Status == "Dispatching")
                .ToListAsync(cancellationToken);

            foreach (var job in staleJobs)
            {
                job.Status = "Pending";
            }

            if (staleJobs.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Recovered {Count} stale in-flight dispatch jobs back to Pending state.", staleJobs.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to recover stale dispatch jobs on startup.");
        }
    }

    private async Task HandleGracefulShutdownAsync()
    {
        using var lease = maintenance.TryEnter(worker: true);
        if (lease == null) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var inFlight = await db.DispatchJobs
                .Where(j => j.Status == "Dispatching")
                .ToListAsync(CancellationToken.None);

            foreach (var job in inFlight)
            {
                job.Status = "Pending";
            }

            if (inFlight.Count > 0)
            {
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogInformation("Graceful shutdown: reset {Count} active dispatch jobs to Pending.", inFlight.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error resetting in-flight jobs during graceful shutdown.");
        }
    }
}
