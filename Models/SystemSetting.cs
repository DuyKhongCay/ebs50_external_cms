namespace ebs50_backend.Models;

/// <summary>
/// Key-value persistent configuration for system settings.
/// </summary>
public class SystemSetting
{
    // NOTE: Primary key setting key (e.g. "Ebs50Host", "RemoteInputPath")
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

