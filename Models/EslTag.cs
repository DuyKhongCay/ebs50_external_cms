namespace ebs50_backend.Models;

/// <summary>
/// Represents an Electronic Shelf Label (ESL) tag deployed on a machine/line.
/// </summary>
public class EslTag
{
    // NOTE: Primary key is the 8-character hexadecimal MAC address (e.g. "B4B819DE")
    public string MacAddress { get; set; } = string.Empty;

    public string MachineNo { get; set; } = string.Empty;

    // Foreign key to ModelItem
    public string ModelCode { get; set; } = string.Empty;

    // Hardware model variant of the ESL tag (e.g. "SE420RY" for 4.2" 3-color e-paper)
    public string Variant { get; set; } = "SE420RY";

    // Foreign key to MachineState
    public int CurrentStateCode { get; set; }

    public int BatteryLevel { get; set; } = 100;

    public DateTime? LastSyncedAt { get; set; }

    public DateTime? LastUploadedAt { get; set; }

    public DateTime? LastConfirmedAt { get; set; }

    // Versioning for optimistic concurrency and deduplication
    public int DesiredRevision { get; set; } = 1;

    public int ConfirmedRevision { get; set; } = 0;

    public int BindingVersion { get; set; } = 1;

    // NOTE: Sync status tracked as "Pending", "Dispatching", "Uploaded", "Synced", or "Error"
    public string SyncStatus { get; set; } = "Pending";

    // Navigation properties
    public ModelItem? Model { get; set; }

    public MachineState? CurrentState { get; set; }
}

