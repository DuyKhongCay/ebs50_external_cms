using ebs50_backend.DTOs;
using ebs50_backend.Services.Core;
using Microsoft.AspNetCore.Mvc;

namespace ebs50_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TagsController : ControllerBase
{
    private readonly ITagManager _tagManager;
    private readonly ILogger<TagsController> _logger;

    public TagsController(ITagManager tagManager, ILogger<TagsController> logger)
    {
        _tagManager = tagManager;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves all E-Tags with optional search and filter criteria.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TagDetailDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTags(
        [FromQuery] string? search = null,
        [FromQuery] string? model = null,
        [FromQuery] int? state = null,
        CancellationToken cancellationToken = default)
    {
        var tags = await _tagManager.GetTagsAsync(search, model, state, cancellationToken);
        return Ok(new ApiResponse<IReadOnlyList<TagDetailDto>>(true, $"Found {tags.Count} tags.", tags));
    }

    /// <summary>
    /// Retrieves detailed status of a specific E-Tag by its MAC address.
    /// </summary>
    [HttpGet("{mac}")]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTagByMac(string mac, CancellationToken cancellationToken = default)
    {
        var tag = await _tagManager.GetTagByMacAsync(mac, cancellationToken);
        if (tag == null)
        {
            return NotFound(new ApiResponse<TagDetailDto>(false, $"Tag with MAC '{mac}' not found."));
        }
        return Ok(new ApiResponse<TagDetailDto>(true, "Tag found.", tag));
    }

    /// <summary>
    /// Changes machine state for an E-Tag by its MAC address and triggers immediate dispatch to EBS-50.
    /// Used by handheld barcode scanners or direct tag maintenance.
    /// </summary>
    [HttpPost("{mac}/state")]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateState(
        string mac,
        [FromBody] UpdateStateByMacRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _tagManager.ChangeStateByMacAsync(mac, request.StateCode, cancellationToken);
            return Ok(new ApiResponse<TagDetailDto>(true, $"Đã cập nhật trạng thái thẻ {mac} thành [{request.StateCode}] và đưa vào hàng đợi bắn sang EBS-50.", updated));
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
    /// Links or re-links an E-Tag MAC address to a Machine number and Product Model.
    /// </summary>
    [HttpPost("link")]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<TagDetailDto>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LinkTag(
        [FromBody] LinkTagRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var linked = await _tagManager.LinkTagAsync(request, cancellationToken);
            return Ok(new ApiResponse<TagDetailDto>(true, $"Đã ghép thẻ {linked.MacAddress} thành công cho Máy {linked.MachineNo} (Model: {linked.ModelCode}).", linked));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiResponse<TagDetailDto>(false, ex.Message));
        }
    }

    /// <summary>
    /// Unlinks and removes an E-Tag from the system.
    /// </summary>
    [HttpDelete("{mac}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnlinkTag(string mac, CancellationToken cancellationToken = default)
    {
        var result = await _tagManager.UnlinkTagAsync(mac, cancellationToken);
        if (!result)
        {
            return NotFound(new ApiResponse<bool>(false, $"Tag with MAC '{mac}' not found."));
        }
        return Ok(new ApiResponse<bool>(true, $"Đã hủy ghép nối thẻ {mac} thành công.", true));
    }
}

