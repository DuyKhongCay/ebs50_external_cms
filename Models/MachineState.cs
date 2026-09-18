namespace ebs50_backend.Models;

/// <summary>
/// Represents a machine production state with localized names and UI metadata.
/// </summary>
public class MachineState
{
    // NOTE: Primary key represents the numeric state code (0: Running, 1: No plan, etc.)
    public int StateCode { get; set; }

    public string StateNameVi { get; set; } = string.Empty;

    public string StateNameKo { get; set; } = string.Empty;

    public string IconFileName { get; set; } = string.Empty;

    // NOTE: Supported theme colors on 3-color e-paper displays: "Black" or "Red"
    public string ThemeColor { get; set; } = "Black";
}

