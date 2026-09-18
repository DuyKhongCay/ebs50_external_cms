using System.ComponentModel.DataAnnotations;

namespace ebs50_backend.DTOs;

/// <summary>
/// Request payload to update machine state metadata.
/// </summary>
public sealed class UpdateMachineStateRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Vietnamese state name cannot be empty.")]
    public string StateNameVi { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Korean state name cannot be empty.")]
    public string StateNameKo { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^(Red|Yellow|Black)$", ErrorMessage = "ThemeColor must be Red, Yellow, or Black.")]
    public string ThemeColor { get; set; } = "Black";
}

/// <summary>
/// Request payload to create a new machine state.
/// </summary>
public sealed class CreateMachineStateRequest
{
    [Required]
    [Range(0, 999, ErrorMessage = "StateCode must be between 0 and 999.")]
    public int StateCode { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Tên trạng thái tiếng Việt không được để trống.")]
    public string StateNameVi { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Tên trạng thái tiếng Hàn không được để trống.")]
    public string StateNameKo { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^(Red|Yellow|Black)$", ErrorMessage = "ThemeColor must be Red, Yellow, or Black.")]
    public string ThemeColor { get; set; } = "Black";

    public string? IconFileName { get; set; }
}

/// <summary>
/// Request payload to create a new product model.
/// </summary>
public sealed class CreateModelRequest
{
    [Required]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "ModelCode cannot be empty.")]
    public string ModelCode { get; set; } = string.Empty;

    [StringLength(255)]
    public string? Description { get; set; }
}

