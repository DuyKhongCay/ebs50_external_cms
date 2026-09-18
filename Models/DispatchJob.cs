namespace ebs50_backend.Models;

/// <summary>
/// Persistent dispatch job record implementing Transactional Outbox pattern.
/// Guarantees that dispatch tasks are not lost across server restarts.
/// </summary>
public class DispatchJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string MacAddress { get; set; } = string.Empty;

    public string ModelCode { get; set; } = string.Empty;

    public int StateCode { get; set; }

    public string MachineNo { get; set; } = string.Empty;

    public string? OverrideThemeColor { get; set; }

    public int DesiredRevision { get; set; }

    public int BindingVersion { get; set; }

    /// <summary>
    /// Pending, Dispatching, Uploaded, Confirmed, Failed, Superseded
    /// </summary>
    public string Status { get; set; } = "Pending";

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    public DateTime? UploadedAt { get; set; }

    public DateTime? ConfirmedAt { get; set; }
}

