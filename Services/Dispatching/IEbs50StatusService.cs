namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Status record of a physical ESL tag reported by the Opticon EBS-50 base station.
/// </summary>
public sealed record Ebs50LabelStatus(
    string MacAddress,
    string ImageFile,
    int ImageId,
    int ImageIdLocal,
    int Status);

/// <summary>
/// Service contract for querying live tag status from the EBS-50 station.
/// </summary>
public interface IEbs50StatusService
{
    /// <summary>
    /// Queries the EBS-50 station SQLite database for current tag transmission and display statuses.
    /// </summary>
    Task<IReadOnlyDictionary<string, Ebs50LabelStatus>> GetLabelStatusesAsync(CancellationToken cancellationToken = default);
}

