using ebs50_backend.Data;
using ebs50_backend.DTOs;
using ebs50_backend.Models;
using ebs50_backend.Services.Dispatching;
using Microsoft.EntityFrameworkCore;

namespace ebs50_backend.Services.Core;

/// <summary>
/// Implementation of ITagService providing direct database access and CRUD operations.
/// </summary>
public class TagService : ITagService
{
    private readonly AppDbContext _dbContext;
    private readonly IEbs50LinkSyncService _linkSyncService;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TagService> _logger;

    public TagService(
        AppDbContext dbContext,
        IEbs50LinkSyncService linkSyncService,
        IWebHostEnvironment env,
        ILogger<TagService> logger)
    {
        _dbContext = dbContext;
        _linkSyncService = linkSyncService;
        _env = env;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EslTag>> GetAllTagsAsync(
        string? search = null,
        string? modelCode = null,
        int? stateCode = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.EslTags
            .AsNoTracking()
            .Include(t => t.CurrentState)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(t => t.MacAddress.ToLower().Contains(s) || t.MachineNo.ToLower().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(modelCode))
        {
            query = query.Where(t => t.ModelCode == modelCode.Trim());
        }

        if (stateCode.HasValue)
        {
            query = query.Where(t => t.CurrentStateCode == stateCode.Value);
        }

        return await query.OrderBy(t => t.MachineNo).ToListAsync(cancellationToken);
    }

    public async Task<EslTag?> GetTagByMacAsync(string mac, CancellationToken cancellationToken = default)
    {
        return await _dbContext.EslTags
            .AsNoTracking()
            .Include(t => t.CurrentState)
            .FirstOrDefaultAsync(t => t.MacAddress == mac.Trim(), cancellationToken);
    }

    public async Task<EslTag?> GetTagByMachineNoAsync(string machineNo, CancellationToken cancellationToken = default)
    {
        return await _dbContext.EslTags
            .AsNoTracking()
            .Include(t => t.CurrentState)
            .FirstOrDefaultAsync(t => t.MachineNo == machineNo.Trim(), cancellationToken);
    }

    public async Task<EslTag> UpdateTagStateAsync(string mac, int newStateCode, CancellationToken cancellationToken = default)
    {
        var cleanMac = mac.Trim();

        var tag = await _dbContext.EslTags
            .Include(t => t.CurrentState)
            .FirstOrDefaultAsync(t => t.MacAddress == cleanMac, cancellationToken);

        if (tag == null)
        {
            throw new KeyNotFoundException($"E-Tag with MAC address '{mac}' not found.");
        }

        var stateExists = await _dbContext.MachineStates
            .AsNoTracking()
            .AnyAsync(s => s.StateCode == newStateCode, cancellationToken);

        if (!stateExists)
        {
            throw new ArgumentException($"Machine state code '{newStateCode}' is invalid.");
        }

        // NOTE: Monotonically increment revision to track delivery updates and supersede older pending jobs
        tag.CurrentStateCode = newStateCode;
        tag.DesiredRevision += 1;
        tag.SyncStatus = "Pending";

        var pendingJobs = await _dbContext.DispatchJobs
            .Where(j => j.MacAddress == cleanMac && j.Status == "Pending")
            .ToListAsync(cancellationToken);

        foreach (var pj in pendingJobs)
        {
            pj.Status = "Superseded";
        }

        // NOTE: Persist DispatchJob in the same transaction (Transactional Outbox) for atomic dispatch guarantee
        var dispatchJob = new DispatchJob
        {
            Id = Guid.NewGuid(),
            MacAddress = cleanMac,
            ModelCode = tag.ModelCode,
            StateCode = newStateCode,
            MachineNo = tag.MachineNo,
            DesiredRevision = tag.DesiredRevision,
            BindingVersion = tag.BindingVersion,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow
        };
        _dbContext.DispatchJobs.Add(dispatchJob);

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Updated state of Tag {Mac} to {StateCode} (Revision: {Rev}, JobId: {JobId})",
            cleanMac, newStateCode, tag.DesiredRevision, dispatchJob.Id);

        return tag;
    }

    public async Task<EslTag> LinkOrUpdateTagAsync(
        string mac,
        string machineNo,
        string modelCode,
        int initialStateCode,
        CancellationToken cancellationToken = default)
    {
        var cleanMac = mac.Trim().ToUpperInvariant();
        var cleanMachine = machineNo.Trim();
        var cleanModel = modelCode.Trim();

        var stateExists = await _dbContext.MachineStates.AnyAsync(s => s.StateCode == initialStateCode, cancellationToken);
        if (!stateExists)
        {
            throw new ArgumentException($"Initial state code '{initialStateCode}' is invalid.");
        }

        // NOTE: Automatically provision new ModelItem to prevent foreign key constraint violation
        var modelExists = await _dbContext.ModelItems.AnyAsync(m => m.ModelCode == cleanModel, cancellationToken);
        if (!modelExists)
        {
            _dbContext.ModelItems.Add(new ModelItem
            {
                ModelCode = cleanModel,
                Description = "Auto registered via Link Tag",
                UpdatedAt = DateTime.UtcNow
            });
        }

        var existingTag = await _dbContext.EslTags.FirstOrDefaultAsync(t => t.MacAddress == cleanMac, cancellationToken);
        if (existingTag != null)
        {
            existingTag.MachineNo = cleanMachine;
            existingTag.ModelCode = cleanModel;
            existingTag.CurrentStateCode = initialStateCode;
            existingTag.BindingVersion += 1;
            existingTag.DesiredRevision = 1;
            existingTag.ConfirmedRevision = 0;
            existingTag.SyncStatus = "Pending";
            _logger.LogInformation("Re-linked existing Tag {Mac} to Machine {Machine}, Model {Model} (BindingVersion: {BVer})",
                cleanMac, cleanMachine, cleanModel, existingTag.BindingVersion);
        }
        else
        {
            existingTag = new EslTag
            {
                MacAddress = cleanMac,
                MachineNo = cleanMachine,
                ModelCode = cleanModel,
                CurrentStateCode = initialStateCode,
                BatteryLevel = 100,
                BindingVersion = 1,
                DesiredRevision = 1,
                ConfirmedRevision = 0,
                SyncStatus = "Pending",
                LastSyncedAt = null
            };
            _dbContext.EslTags.Add(existingTag);
            _logger.LogInformation("Linked new Tag {Mac} to Machine {Machine}, Model {Model}", cleanMac, cleanMachine, cleanModel);
        }

        var staleJobs = await _dbContext.DispatchJobs
            .Where(j => j.MacAddress == cleanMac && j.Status == "Pending")
            .ToListAsync(cancellationToken);
        foreach (var sj in staleJobs)
        {
            sj.Status = "Superseded";
        }

        var initialJob = new DispatchJob
        {
            Id = Guid.NewGuid(),
            MacAddress = cleanMac,
            ModelCode = cleanModel,
            StateCode = initialStateCode,
            MachineNo = cleanMachine,
            DesiredRevision = 1,
            BindingVersion = existingTag.BindingVersion,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow
        };
        _dbContext.DispatchJobs.Add(initialJob);

        await _dbContext.SaveChangesAsync(cancellationToken);

        // NOTE: Sync links.csv to EBS-50 so the tag binding is recognized by Opticon station firmware
        try
        {
            await _linkSyncService.SyncAllLinksAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to synchronize links.csv to EBS-50 during LinkTag for {Mac}. Dispatch worker will retry or proceed.", cleanMac);
        }

        return existingTag;
    }

    public async Task<bool> UnlinkTagAsync(string mac, CancellationToken cancellationToken = default)
    {
        var cleanMac = mac.Trim();
        var tag = await _dbContext.EslTags.FirstOrDefaultAsync(t => t.MacAddress == cleanMac, cancellationToken);
        if (tag == null) return false;

        // NOTE: Supersede pending jobs so the background worker will not dispatch an unlinked tag
        var pendingJobs = await _dbContext.DispatchJobs
            .Where(j => j.MacAddress == cleanMac && j.Status == "Pending")
            .ToListAsync(cancellationToken);
        foreach (var pj in pendingJobs)
        {
            pj.Status = "Superseded";
        }

        _dbContext.EslTags.Remove(tag);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Unlinked/Deleted Tag {Mac} and superseded pending jobs", cleanMac);

        try
        {
            await _linkSyncService.SyncAllLinksAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to synchronize links.csv to EBS-50 during UnlinkTag for {Mac}", cleanMac);
        }

        return true;
    }

    public async Task<IReadOnlyList<MachineState>> GetAllStatesAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.MachineStates
            .AsNoTracking()
            .OrderBy(s => s.StateCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<MachineState?> GetStateByCodeAsync(int stateCode, CancellationToken cancellationToken = default)
    {
        return await _dbContext.MachineStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.StateCode == stateCode, cancellationToken);
    }

    public async Task<MachineState> CreateStateAsync(
        CreateMachineStateRequest request,
        Stream? iconStream = null,
        string? iconFileName = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.MachineStates.FirstOrDefaultAsync(s => s.StateCode == request.StateCode, cancellationToken);
        if (existing != null)
        {
            throw new InvalidOperationException($"Mã trạng thái [{request.StateCode}] đã tồn tại.");
        }

        string finalIconName = "running.png";

        if (iconStream != null && !string.IsNullOrWhiteSpace(iconFileName))
        {
            var ext = Path.GetExtension(iconFileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || (ext != ".png" && ext != ".jpg" && ext != ".bmp"))
            {
                ext = ".png";
            }
            finalIconName = $"state_{request.StateCode}{ext}";

            var targetDirs = new List<string>
            {
                Path.Combine(_env.ContentRootPath, "Assets", "Templates"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Templates")
            };

            foreach (var dir in targetDirs)
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var destPath = Path.Combine(dir, finalIconName);
                iconStream.Position = 0;
                using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write);
                await iconStream.CopyToAsync(fileStream, cancellationToken);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.IconFileName))
        {
            finalIconName = request.IconFileName.Trim();
        }

        var newState = new MachineState
        {
            StateCode = request.StateCode,
            StateNameVi = request.StateNameVi.Trim(),
            StateNameKo = request.StateNameKo.Trim(),
            ThemeColor = request.ThemeColor,
            IconFileName = finalIconName
        };

        _dbContext.MachineStates.Add(newState);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Created new machine state: [{Code}] {NameVi}", newState.StateCode, newState.StateNameVi);

        return newState;
    }

    public async Task<MachineState> UpdateStateAsync(
        int stateCode,
        UpdateMachineStateRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = await _dbContext.MachineStates.FirstOrDefaultAsync(s => s.StateCode == stateCode, cancellationToken);
        if (state == null)
        {
            throw new KeyNotFoundException($"Machine state '{stateCode}' not found.");
        }

        state.StateNameVi = request.StateNameVi.Trim();
        state.StateNameKo = request.StateNameKo.Trim();
        state.ThemeColor = request.ThemeColor;

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Updated metadata for state [{Code}]", stateCode);
        return state;
    }

    public async Task<bool> DeleteStateAsync(int stateCode, CancellationToken cancellationToken = default)
    {
        var state = await _dbContext.MachineStates.FirstOrDefaultAsync(s => s.StateCode == stateCode, cancellationToken);
        if (state == null) return false;

        var inUse = await _dbContext.EslTags.AnyAsync(t => t.CurrentStateCode == stateCode, cancellationToken);
        if (inUse)
        {
            throw new InvalidOperationException($"Không thể xóa trạng thái [{stateCode}] vì đang có thẻ nhãn sử dụng.");
        }

        _dbContext.MachineStates.Remove(state);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted machine state: [{Code}]", stateCode);
        return true;
    }

    public async Task<string?> GetStateIconPathAsync(int stateCode, CancellationToken cancellationToken = default)
    {
        var state = await _dbContext.MachineStates.AsNoTracking().FirstOrDefaultAsync(s => s.StateCode == stateCode, cancellationToken);
        var iconFile = state?.IconFileName;
        if (string.IsNullOrWhiteSpace(iconFile)) iconFile = "running.png";

        var candidates = new[]
        {
            Path.Combine(_env.ContentRootPath, "Assets", "Templates", iconFile),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", iconFile),
            Path.Combine(_env.ContentRootPath, "Assets", "Templates", "running.png")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<string> SaveStateIconAsync(
        int stateCode,
        Stream stream,
        string originalFileName,
        CancellationToken cancellationToken = default)
    {
        var state = await _dbContext.MachineStates.FirstOrDefaultAsync(s => s.StateCode == stateCode, cancellationToken);
        if (state == null)
        {
            throw new KeyNotFoundException($"Machine state '{stateCode}' not found.");
        }

        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || (ext != ".png" && ext != ".jpg" && ext != ".bmp"))
        {
            ext = ".png";
        }

        var safeFileName = $"state_{stateCode}{ext}";

        // Save in both ContentRoot and AppContext.BaseDirectory if needed
        var targetDirs = new List<string>
        {
            Path.Combine(_env.ContentRootPath, "Assets", "Templates"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Templates")
        };

        foreach (var dir in targetDirs)
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var destPath = Path.Combine(dir, safeFileName);
            stream.Position = 0;
            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(fileStream, cancellationToken);
        }

        state.IconFileName = safeFileName;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Uploaded and saved new icon {FileName} for state [{StateCode}]", safeFileName, stateCode);
        return safeFileName;
    }

    public async Task<IReadOnlyList<ModelItem>> GetAllModelsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.ModelItems
            .AsNoTracking()
            .OrderBy(m => m.ModelCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<ModelItem> AddModelAsync(CreateModelRequest request, CancellationToken cancellationToken = default)
    {
        var cleanCode = request.ModelCode.Trim();
        var existing = await _dbContext.ModelItems.FirstOrDefaultAsync(m => m.ModelCode == cleanCode, cancellationToken);
        if (existing != null)
        {
            existing.Description = request.Description;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new ModelItem
            {
                ModelCode = cleanCode,
                Description = request.Description,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.ModelItems.Add(existing);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Saved model item: {Code}", cleanCode);
        return existing;
    }

    public async Task<bool> DeleteModelAsync(string modelCode, CancellationToken cancellationToken = default)
    {
        var model = await _dbContext.ModelItems.FirstOrDefaultAsync(m => m.ModelCode == modelCode.Trim(), cancellationToken);
        if (model == null) return false;

        _dbContext.ModelItems.Remove(model);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted model: {Code}", modelCode);
        return true;
    }
}
