using ebs50_backend.DTOs;
using ebs50_backend.Models;
using ebs50_backend.Services.Core;
using Microsoft.AspNetCore.Mvc;

namespace ebs50_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModelsController : ControllerBase
{
    private readonly ITagService _tagService;
    private readonly ILogger<ModelsController> _logger;

    public ModelsController(ITagService tagService, ILogger<ModelsController> logger)
    {
        _tagService = tagService;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves all registered product models.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ModelItem>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllModels(CancellationToken cancellationToken = default)
    {
        var models = await _tagService.GetAllModelsAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyList<ModelItem>>(true, $"Loaded {models.Count} models.", models));
    }

    /// <summary>
    /// Adds or updates a product model in the registry.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ModelItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddModel([FromBody] CreateModelRequest request, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var model = await _tagService.AddModelAsync(request, cancellationToken);
        return Ok(new ApiResponse<ModelItem>(true, $"Đã lưu Model '{model.ModelCode}' thành công.", model));
    }

    /// <summary>
    /// Deletes a product model from the registry.
    /// </summary>
    [HttpDelete("{modelCode}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteModel(string modelCode, CancellationToken cancellationToken = default)
    {
        var deleted = await _tagService.DeleteModelAsync(modelCode, cancellationToken);
        if (!deleted)
        {
            return NotFound(new ApiResponse<bool>(false, $"Model '{modelCode}' not found."));
        }
        return Ok(new ApiResponse<bool>(true, $"Đã xóa Model '{modelCode}' thành công.", true));
    }
}

