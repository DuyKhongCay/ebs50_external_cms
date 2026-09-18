using ebs50_backend.DTOs;
using ebs50_backend.Services.Core;
using Microsoft.AspNetCore.Mvc;

namespace ebs50_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MachinesController : ControllerBase
{
    private readonly ITagManager _tagManager;
    private readonly ILogger<MachinesController> _logger;

    public MachinesController(ITagManager tagManager, ILogger<MachinesController> logger)
    {
        _tagManager = tagManager;
        _logger = logger;
    }

    /// <summary>
    /// Changes machine state by Machine Number and triggers background render and SFTP dispatch to EBS-50.
    /// Primary endpoint used for MES (Manufacturing Execution System) production automation.
    /// </summary>
    /// <remarks>HTTP 200 means the backend accepted the state change. Rendering, upload and physical display happen later; inspect SyncStatus separately.</remarks>
    /// <param name="machineNo">Machine number already bound to a tag; use a URL-safe identifier without slashes.</param>
    /// <param name="request">Desired StateCode from GET /api/States.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>Backend tag snapshot, not physical delivery confirmation.</returns>
    [HttpPost("{machineNo}/state")]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMachineState(
        string machineNo,
        [FromBody] UpdateStateByMachineRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _tagManager.ChangeStateByMachineNoAsync(machineNo, request.StateCode, cancellationToken);
            _logger.LogInformation("MES updated Machine {Machine} to state [{State}]", machineNo, request.StateCode);

            return Ok(new ApiResponse<TagDetailDto>(
                true,
                $"[MES] Đã cập nhật trạng thái Máy {machineNo} thành [{request.StateCode}] và đưa vào hàng đợi bắn sang EBS-50.",
                updated));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiResponse<TagDetailDto>(false, ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiResponse<TagDetailDto>(false, ex.Message));
        }
    }

    /// <summary>
    /// Retrieves the E-Tag linked to a specific machine number.
    /// </summary>
    [HttpGet("{machineNo}/tag")]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTagByMachine(string machineNo, CancellationToken cancellationToken = default)
    {
        var tag = await _tagManager.GetTagByMachineNoAsync(machineNo, cancellationToken);
        if (tag == null)
        {
            return NotFound(new ApiResponse<TagDetailDto>(false, $"No E-Tag linked to machine '{machineNo}'."));
        }
        return Ok(new ApiResponse<TagDetailDto>(true, "Machine tag found.", tag));
    }
}
