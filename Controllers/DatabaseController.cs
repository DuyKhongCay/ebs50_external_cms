using ebs50_backend.DTOs;
using ebs50_backend.Services.Database;
using Microsoft.AspNetCore.Mvc;

namespace ebs50_backend.Controllers;

/// <summary>Local-only SQLite backup/restore. Mutations require the antiforgery token from /Database.</summary>
[ApiController]
[Route("api/database")]
[AutoValidateAntiforgeryToken]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class DatabaseController(DatabaseMaintenanceService maintenance, DatabaseMaintenanceCoordinator coordinator) : ControllerBase
{
    /// <summary>Reports maintenance and post-restore synchronization state without opening SQLite.</summary>
    [HttpGet("status")]
    public ActionResult<DatabaseMaintenanceStatus> Status() => new DatabaseMaintenanceStatus(coordinator.Unavailable, coordinator.SyncPaused);

    /// <summary>Creates a snapshot archive in a background job. Does not cancel when the caller disconnects after acceptance.</summary>
    [HttpPost("exports")]
    [ProducesResponseType(typeof(DatabaseOperation), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ExportAsync(CancellationToken cancellationToken) => AcceptAsync(() => maintenance.QueueExportAsync(cancellationToken));

    /// <summary>Downloads a completed export; archives expire after 24 hours.</summary>
    [HttpGet("exports/{id:guid}/download")]
    [Produces("application/zip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Download(Guid id)
    {
        var download = maintenance.OpenDownload(id);
        return download == null ? NotFound() : File(download.Value.Stream, "application/zip", download.Value.Name);
    }

    /// <summary>Uploads a ZIP for validation and preview. Use the returned operation ID for restore within one hour.</summary>
    [HttpPost("imports/validate")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(DatabaseBackupFiles.MaxUploadBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = DatabaseBackupFiles.MaxUploadBytes)]
    [ProducesResponseType(typeof(DatabaseOperation), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> ValidateAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0) return BadRequest(new { error = "Chọn file ZIP backup." });
        if (file.Length > DatabaseBackupFiles.MaxUploadBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        await using var input = file.OpenReadStream();
        return await AcceptAsync(() => maintenance.QueueValidationAsync(input, cancellationToken));
    }

    /// <summary>Consumes a validated upload and replaces all database data after taking a recovery backup.</summary>
    [HttpPost("imports/{id:guid}/restore")]
    [ProducesResponseType(typeof(DatabaseOperation), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> RestoreAsync(Guid id, CancellationToken cancellationToken) => AcceptAsync(() => maintenance.QueueRestoreAsync(id, cancellationToken));

    /// <summary>Reads durable operation status, including validation metadata and the local recovery backup path.</summary>
    [HttpGet("operations/{id:guid}")]
    [ProducesResponseType(typeof(DatabaseOperation), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Operation(Guid id)
    {
        var operation = maintenance.GetOperation(id);
        return operation == null ? NotFound() : Ok(operation);
    }

    /// <summary>Supersedes unfinished historical jobs and creates new dispatch jobs for all current tags, then resumes both workers.</summary>
    [HttpPost("resume-sync")]
    [ProducesResponseType(typeof(DatabaseOperation), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ResumeAsync(CancellationToken cancellationToken) => AcceptAsync(() => maintenance.QueueResumeAsync(cancellationToken));

    private async Task<IActionResult> AcceptAsync(Func<Task<DatabaseOperation>> action)
    {
        try
        {
            var operation = await action();
            return AcceptedAtAction(nameof(Operation), new { id = operation.Id }, operation);
        }
        catch (DatabaseMaintenanceBusyException ex) { return Conflict(new { error = ex.Message }); }
        catch (InvalidDataException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
