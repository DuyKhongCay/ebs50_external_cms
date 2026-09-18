using ebs50_backend.Data;
using ebs50_backend.Services.Rendering;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RenderController(
    AppDbContext dbContext,
    ITagRenderService renderService,
    ILogger<RenderController> logger) : ControllerBase
{
    /// <summary>
    /// Renders live preview image for a deployed ESL tag identified by MAC address.
    /// </summary>
    [HttpGet("preview/{mac}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTagPreviewAsync(string mac, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return BadRequest("MAC address cannot be empty.");
        }

        logger.LogInformation("Rendering live preview for tag MAC: {Mac}", mac);

        var tag = await dbContext.EslTags
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.MacAddress == mac, cancellationToken);

        if (tag == null)
        {
            return NotFound($"Tag with MAC address '{mac}' was not found.");
        }

        var request = new TagRenderRequest(
            MacAddress: tag.MacAddress,
            ModelCode: tag.ModelCode,
            StateCode: tag.CurrentStateCode,
            MachineNo: tag.MachineNo);

        var result = await renderService.RenderTagImageAsync(request, cancellationToken);
        return File(result.ImageBytes, result.ContentType);
    }

    /// <summary>
    /// Simulates rendering for arbitrary model, state code, and theme color (White, Black, Red, Yellow).
    /// </summary>
    [HttpGet("simulate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SimulateRenderAsync(
        [FromQuery] string? model = "A27/M47/F70",
        [FromQuery] int state = 0,
        [FromQuery] string? color = "Red",
        CancellationToken cancellationToken = default)
    {
        var request = new TagRenderRequest(
            MacAddress: "SIMULATE",
            ModelCode: model ?? "A27/M47/F70",
            StateCode: state,
            OverrideThemeColor: color);

        var result = await renderService.RenderTagImageAsync(request, cancellationToken);
        return File(result.ImageBytes, result.ContentType);
    }
}
