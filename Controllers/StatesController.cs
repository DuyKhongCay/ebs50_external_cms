using ebs50_backend.DTOs;
using ebs50_backend.Models;
using ebs50_backend.Services.Core;
using Microsoft.AspNetCore.Mvc;

namespace ebs50_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatesController : ControllerBase
{
    private readonly ITagService _tagService;
    private readonly ILogger<StatesController> _logger;

    public StatesController(ITagService tagService, ILogger<StatesController> logger)
    {
        _tagService = tagService;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves all configured machine states.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MachineState>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllStates(CancellationToken cancellationToken = default)
    {
        var states = await _tagService.GetAllStatesAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyList<MachineState>>(true, $"Loaded {states.Count} states.", states));
    }

    /// <summary>
    /// Retrieves a specific machine state by code.
    /// </summary>
    [HttpGet("{stateCode}")]
    [ProducesResponseType(typeof(ApiResponse<MachineState>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<MachineState>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStateByCode(int stateCode, CancellationToken cancellationToken = default)
    {
        var state = await _tagService.GetStateByCodeAsync(stateCode, cancellationToken);
        if (state == null)
        {
            return NotFound(new ApiResponse<MachineState>(false, $"State [{stateCode}] not found."));
        }
        return Ok(new ApiResponse<MachineState>(true, "State found.", state));
    }

    /// <summary>
    /// Updates the metadata (Vietnamese, Korean names, theme color) of a machine state.
    /// </summary>
    [HttpPut("{stateCode}")]
    [ProducesResponseType(typeof(ApiResponse<MachineState>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<MachineState>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateState(
        int stateCode,
        [FromBody] UpdateMachineStateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _tagService.UpdateStateAsync(stateCode, request, cancellationToken);
            return Ok(new ApiResponse<MachineState>(true, $"Đã cập nhật thông tin trạng thái [{stateCode}] thành công.", updated));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiResponse<MachineState>(false, ex.Message));
        }
    }

    /// <summary>
    /// Serves the image icon file of a machine state directly to browsers.
    /// </summary>
    [HttpGet("{stateCode}/icon")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStateIcon(int stateCode, CancellationToken cancellationToken = default)
    {
        var iconPath = await _tagService.GetStateIconPathAsync(stateCode, cancellationToken);
        if (string.IsNullOrEmpty(iconPath) || !System.IO.File.Exists(iconPath))
        {
            return NotFound("Icon not found.");
        }

        var ext = Path.GetExtension(iconPath).ToLowerInvariant();
        var mime = ext == ".bmp" ? "image/bmp" : (ext == ".jpg" || ext == ".jpeg") ? "image/jpeg" : "image/png";
        return PhysicalFile(iconPath, mime);
    }

    /// <summary>
    /// Creates a new machine state with optional icon file upload.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<MachineState>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<MachineState>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateState(
        [FromForm] CreateMachineStateRequest request,
        IFormFile? iconFile,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            Stream? stream = null;
            string? fileName = null;
            if (iconFile != null && iconFile.Length > 0)
            {
                stream = iconFile.OpenReadStream();
                fileName = iconFile.FileName;
            }

            var created = await _tagService.CreateStateAsync(request, stream, fileName, cancellationToken);
            return Ok(new ApiResponse<MachineState>(true, $"Đã tạo trạng thái mới [{created.StateCode}] {created.StateNameVi} thành công.", created));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<MachineState>(false, ex.Message));
        }
    }

    /// <summary>
    /// Deletes a custom machine state if no active E-Tags are currently using it.
    /// </summary>
    [HttpDelete("{stateCode}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteState(int stateCode, CancellationToken cancellationToken = default)
    {
        try
        {
            var deleted = await _tagService.DeleteStateAsync(stateCode, cancellationToken);
            if (!deleted)
            {
                return NotFound(new ApiResponse<bool>(false, $"Trạng thái [{stateCode}] không tồn tại."));
            }
            return Ok(new ApiResponse<bool>(true, $"Đã xóa trạng thái [{stateCode}] thành công.", true));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<bool>(false, ex.Message));
        }
    }

    /// <summary>
    /// Uploads and assigns a new PNG/image icon to a machine state.
    /// </summary>
    [HttpPost("{stateCode}/icon")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadIcon(
        int stateCode,
        IFormFile? iconFile,
        CancellationToken cancellationToken = default)
    {
        if (iconFile == null || iconFile.Length == 0)
        {
            return BadRequest(new ApiResponse<string>(false, "Vui lòng chọn file ảnh icon hợp lệ."));
        }

        try
        {
            using var stream = iconFile.OpenReadStream();
            var fileName = await _tagService.SaveStateIconAsync(stateCode, stream, iconFile.FileName, cancellationToken);
            _logger.LogInformation("Successfully uploaded new icon {FileName} for state [{Code}]", fileName, stateCode);

            return Ok(new ApiResponse<string>(true, $"Đã tải lên và cập nhật icon cho trạng thái [{stateCode}] thành công.", fileName));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiResponse<string>(false, ex.Message));
        }
    }
}

