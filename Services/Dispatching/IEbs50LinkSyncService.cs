namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Service contract for synchronizing ESL tag link mappings (links.csv) with the EBS-50 station.
/// </summary>
public interface IEbs50LinkSyncService
{
    /// <summary>
    /// Generates and uploads the comprehensive links.csv file to the EBS-50 station via SFTP.
    /// Ensures all tags have an active binding in the Opticon station's links table.
    /// </summary>
    Task SyncAllLinksAsync(CancellationToken cancellationToken = default);
}

