using ebs50_backend.Data;
using ebs50_backend.DTOs;
using ebs50_backend.Services.Dispatching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DispatchController(
    AppDbContext dbContext,
    IEslDispatchQueue queue,
    IEbs50SftpService sftpService,
    ILogger<DispatchController> logger) : ControllerBase
{
    /// <summary>
    /// Enqueues a specific tag for rendering and SFTP dispatching to EBS-50.
    /// </summary>
    /// <remarks>202 means persisted in the queue, not uploaded or displayed. Use the machine state API for MES state validation.</remarks>
    /// <param name="mac">Existing tag MAC, as stored in the backend.</param>
    /// <param name="newStateCode">Optional state override in the query string.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>Accepted job and queue snapshot.</returns>
    [HttpPost("send-tag/{mac}")]
    [ProducesResponseType(typeof(DispatchAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DispatchTagAsync(
        string mac,
        [FromQuery] int? newStateCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return BadRequest("MAC address cannot be empty.");
        }

        var tag = await dbContext.EslTags.FirstOrDefaultAsync(t => t.MacAddress == mac, cancellationToken);
        if (tag == null)
        {
            return NotFound($"Tag with MAC address '{mac}' not found in database.");
        }

        if (newStateCode.HasValue && newStateCode.Value != tag.CurrentStateCode)
        {
            tag.CurrentStateCode = newStateCode.Value;
            tag.DesiredRevision += 1;
            tag.SyncStatus = "Pending";
        }

        // NOTE: Supersede previous pending jobs for this MAC to prevent redundant renders and out-of-order state updates.
        var staleJobs = await dbContext.DispatchJobs
            .Where(j => j.MacAddress == tag.MacAddress && j.Status == "Pending")
            .ToListAsync(cancellationToken);
        foreach (var sj in staleJobs)
        {
            sj.Status = "Superseded";
        }

        var job = new Models.DispatchJob
        {
            Id = Guid.NewGuid(),
            MacAddress = tag.MacAddress,
            ModelCode = tag.ModelCode,
            StateCode = tag.CurrentStateCode,
            MachineNo = tag.MachineNo,
            DesiredRevision = tag.DesiredRevision,
            BindingVersion = tag.BindingVersion,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow
        };

        dbContext.DispatchJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        queue.NotifyJobAvailable();
        logger.LogInformation("Enqueued tag {Mac} for dispatching (JobId: {JobId}).", mac, job.Id);

        var pendingCount = await dbContext.DispatchJobs.CountAsync(j => j.Status == "Pending", cancellationToken);

        return Accepted(new DispatchAcceptedResponse(
            $"Tag '{mac}' enqueued for rendering and dispatching.",
            job.Id, mac, tag.ModelCode, tag.CurrentStateCode, pendingCount));
    }

    /// <summary>
    /// Enqueues all configured tags in the database for batch rendering and SFTP dispatching.
    /// </summary>
    [HttpPost("send-all")]
    [ProducesResponseType(typeof(DispatchBatchResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> DispatchAllTagsAsync(CancellationToken cancellationToken)
    {
        var tags = await dbContext.EslTags.ToListAsync(cancellationToken);

        int count = 0;
        foreach (var tag in tags)
        {
            tag.SyncStatus = "Pending";

            var job = new Models.DispatchJob
            {
                Id = Guid.NewGuid(),
                MacAddress = tag.MacAddress,
                ModelCode = tag.ModelCode,
                StateCode = tag.CurrentStateCode,
                MachineNo = tag.MachineNo,
                DesiredRevision = tag.DesiredRevision,
                BindingVersion = tag.BindingVersion,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow
            };

            dbContext.DispatchJobs.Add(job);
            count++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        queue.NotifyJobAvailable();

        logger.LogInformation("Enqueued {Count} tags for batch dispatching.", count);
        return Accepted(new DispatchBatchResponse($"Enqueued {count} tags for dispatching.", count, count));
    }

    /// <summary>
    /// Checks SSH/SFTP connectivity and whether the Input directory exists.
    /// </summary>
    /// <remarks>Does not write a file or verify RF delivery. HTTP 200 can contain Success=false; a missing directory is described in Message even when Success=true.</remarks>
    [HttpPost("test-connection")]
    [ProducesResponseType(typeof(SftpTestResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Testing SFTP connection to EBS-50...");
        var result = await sftpService.TestConnectionAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves current background dispatcher queue metrics from database.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(DispatchQueueStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueStatusAsync(CancellationToken cancellationToken)
    {
        var pendingJobs = await dbContext.DispatchJobs.CountAsync(j => j.Status == "Pending" || j.Status == "Dispatching", cancellationToken);
        return Ok(new DispatchQueueStatusResponse(pendingJobs, DateTime.UtcNow));
    }

    /// <summary>
    /// Retrieves stored EBS-50 settings with controller defaults, not the full transport configuration merge.
    /// </summary>
    /// <remarks>Includes the stored password unmasked. This endpoint currently has no authentication policy.</remarks>
    [HttpGet("config")]
    [ProducesResponseType(typeof(StationConfigResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConfigAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.SystemSettings.AsNoTracking().ToListAsync(cancellationToken);
        string GetVal(string k, string def) => settings.FirstOrDefault(s => s.Key == k)?.Value ?? def;

        return Ok(new StationConfigResponse(
            GetVal("Ebs50Host", "192.168.1.50"),
            int.TryParse(GetVal("Ebs50Port", "22"), out var p) ? p : 22,
            GetVal("Ebs50Username", "root"), GetVal("Ebs50Password", ""),
            GetVal("RemoteInputPath", "/home/root/ebs_50_run/Input")));
    }

    /// <summary>
    /// Updates the EBS-50 connection configuration in the SQLite database.
    /// </summary>
    [HttpPost("config")]
    [ProducesResponseType(typeof(ConfigSavedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveConfigAsync([FromBody] Ebs50ConfigDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Host))
        {
            return BadRequest("Host cannot be empty.");
        }

        async Task UpsertAsync(string key, string val, string desc)
        {
            var item = await dbContext.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
            if (item != null)
            {
                item.Value = val;
                item.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                dbContext.SystemSettings.Add(new Models.SystemSetting
                {
                    Key = key,
                    Value = val,
                    Description = desc,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await UpsertAsync("Ebs50Host", dto.Host.Trim(), "IP or hostname of EBS-50");
        await UpsertAsync("Ebs50Port", dto.Port.ToString(), "SFTP port");
        await UpsertAsync("Ebs50Username", (dto.Username ?? "root").Trim(), "SFTP username");
        if (dto.Password != null)
        {
            await UpsertAsync("Ebs50Password", dto.Password, "SFTP password");
        }
        await UpsertAsync("RemoteInputPath", (dto.RemotePath ?? "/home/root/ebs_50_run/Input").Trim(), "EBS-50 Input folder");

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Updated EBS-50 connection settings: {Host}:{Port}", dto.Host, dto.Port);

        return Ok(new ConfigSavedResponse(true, "Đã lưu cài đặt kết nối EBS-50 thành công!"));
    }
}

/// <summary>Backend connection settings. A null Password keeps the stored value; an empty string saves an empty password.</summary>
public sealed record Ebs50ConfigDto(string Host, int Port, string? Username, string? Password, string? RemotePath);
