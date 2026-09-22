using ebs50_backend.Data;
using Microsoft.EntityFrameworkCore;
using ebs50_backend.Services.Database;

namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Background worker that periodically reconciles tag display statuses with the Opticon EBS-50 station.
/// Closes the feedback loop by transitioning tags and dispatch jobs from 'Uploaded' to 'Confirmed'.
/// Follows csharp-async best practices with non-blocking PeriodicTimer and cancellation support.
/// </summary>
public sealed class Ebs50StatusReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    DatabaseMaintenanceCoordinator maintenance,
    ILogger<Ebs50StatusReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(25);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ebs50StatusReconciliationWorker started (Interval: {Interval}s).", CheckInterval.TotalSeconds);

        using var timer = new PeriodicTimer(CheckInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await ReconcileStatusesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Ebs50StatusReconciliationWorker shutting down gracefully.");
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unhandled error during tag status reconciliation cycle.");
            }
        }
    }

    private async Task ReconcileStatusesAsync(CancellationToken cancellationToken)
    {
        using var lease = maintenance.TryEnter(worker: true);
        if (lease == null) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var statusService = scope.ServiceProvider.GetRequiredService<IEbs50StatusService>();

        // Check if any tags or jobs are awaiting RF transmission confirmation
        var unconfirmedTags = await db.EslTags
            .Where(t => t.SyncStatus == "Uploaded")
            .ToListAsync(cancellationToken);

        if (unconfirmedTags.Count == 0)
        {
            return;
        }

        logger.LogDebug("Reconciling display statuses for {Count} uploaded tags...", unconfirmedTags.Count);

        var liveStatuses = await statusService.GetLabelStatusesAsync(cancellationToken);
        if (liveStatuses.Count == 0)
        {
            return;
        }

        bool hasUpdates = false;

        foreach (var tag in unconfirmedTags)
        {
            if (!liveStatuses.TryGetValue(tag.MacAddress, out var labelStatus))
            {
                continue;
            }

            // NOTE: In Opticon ESL firmware, STATUS == 10 (Transmitted) or STATUS == 0 (Idle/Ok),
            // and ImageId == ImageIdLocal confirms that the image has been rendered and acknowledged on the tag.
            bool isConfirmed = (labelStatus.Status == 10 || labelStatus.Status == 0)
                               && labelStatus.ImageId > 0
                               && labelStatus.ImageId == labelStatus.ImageIdLocal;

            if (isConfirmed)
            {
                tag.SyncStatus = "Confirmed";
                tag.ConfirmedRevision = tag.DesiredRevision;
                tag.LastConfirmedAt = DateTime.UtcNow;
                tag.LastSyncedAt = DateTime.UtcNow;
                hasUpdates = true;

                var pendingJobs = await db.DispatchJobs
                    .Where(j => j.MacAddress == tag.MacAddress && j.Status == "Uploaded")
                    .ToListAsync(cancellationToken);

                foreach (var job in pendingJobs)
                {
                    job.Status = "Confirmed";
                    job.ConfirmedAt = DateTime.UtcNow;
                }

                logger.LogInformation(
                    "Tag {Mac} confirmed displayed on EBS-50! (ImageId: {ImgId}, Status: {Status}, Revision: {Rev})",
                    tag.MacAddress, labelStatus.ImageId, labelStatus.Status, tag.ConfirmedRevision);
            }
        }

        if (hasUpdates)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
