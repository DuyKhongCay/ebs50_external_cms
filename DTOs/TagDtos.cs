using System.ComponentModel.DataAnnotations;

namespace ebs50_backend.DTOs;

/// <summary>
/// Generic API response wrapper.
/// </summary>
public sealed record ApiResponse<T>(bool Success, string Message, T? Data = default);

/// <summary>
/// Request payload to update state by MAC address.
/// </summary>
public sealed class UpdateStateByMacRequest
{
    /// <summary>Machine state code (0–999); it must also exist in the configured state catalog.</summary>
    [Required]
    [Range(0, 999, ErrorMessage = "StateCode must be between 0 and 999.")]
    public int StateCode { get; set; }
}

/// <summary>
/// Request payload to update state by machine number (MES automation).
/// </summary>
public sealed class UpdateStateByMachineRequest
{
    /// <summary>Machine state code (0–999); it must also exist in the configured state catalog.</summary>
    [Required]
    [Range(0, 999, ErrorMessage = "StateCode must be between 0 and 999.")]
    public int StateCode { get; set; }
}

/// <summary>
/// Request payload to link or relink an E-Tag to a machine and product model.
/// </summary>
public sealed class LinkTagRequest
{
    /// <summary>Tag MAC stored in the backend. Use the actual MAC provided by the station.</summary>
    [Required]
    [StringLength(32, MinimumLength = 4, ErrorMessage = "MAC address must be between 4 and 32 characters.")]
    public string MacAddress { get; set; } = string.Empty;

    [Required]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "MachineNo cannot be empty.")]
    public string MachineNo { get; set; } = string.Empty;

    [Required]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "ModelCode cannot be empty.")]
    public string ModelCode { get; set; } = string.Empty;

    [Range(0, 999, ErrorMessage = "Initial state code must be between 0 and 999.")]
    public int InitialStateCode { get; set; } = 0;

    /// <summary>
    /// If true, automatically renders and dispatches to EBS-50 immediately upon linking.
    /// </summary>
    public bool AutoDispatch { get; set; } = true;
}

/// <summary>
/// DTO representing comprehensive information of an E-Tag.
/// </summary>
public sealed record TagDetailDto(
    string MacAddress,
    string MachineNo,
    string ModelCode,
    int StateCode,
    string StateNameVi,
    string StateNameKo,
    string ThemeColor,
    int BatteryLevel,
    string SyncStatus,
    DateTime? LastSyncedAt);
