namespace ebs50_backend.Models;

/// <summary>
/// Represents a product model item produced on the manufacturing line.
/// </summary>
public class ModelItem
{
    // NOTE: Model code acts as the primary key (e.g. "A27/M47/F70", "A18 LTE")
    public string ModelCode { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

