using ebs50_backend.DTOs;
using ebs50_backend.Models;
using ebs50_backend.Services.Dispatching;

namespace ebs50_backend.Services.Core;

/// <summary>
/// Implementation of ITagManager coordinating business workflows, validation, and queue dispatching.
/// </summary>
public class TagManager : ITagManager
{
    private readonly ITagService _tagService;
    private readonly IEslDispatchQueue _dispatchQueue;
    private readonly ILogger<TagManager> _logger;

    public TagManager(
        ITagService tagService,
        IEslDispatchQueue dispatchQueue,
        ILogger<TagManager> logger)
    {
        _tagService = tagService;
        _dispatchQueue = dispatchQueue;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TagDetailDto>> GetTagsAsync(
        string? search = null,
        string? model = null,
        int? state = null,
        CancellationToken cancellationToken = default)
    {
        var tags = await _tagService.GetAllTagsAsync(search, model, state, cancellationToken);
        return tags.Select(MapToDto).ToList();
    }

    public async Task<TagDetailDto?> GetTagByMacAsync(string mac, CancellationToken cancellationToken = default)
    {
        var tag = await _tagService.GetTagByMacAsync(mac, cancellationToken);
        return tag == null ? null : MapToDto(tag);
    }

    public async Task<TagDetailDto?> GetTagByMachineNoAsync(string machineNo, CancellationToken cancellationToken = default)
    {
        var tag = await _tagService.GetTagByMachineNoAsync(machineNo, cancellationToken);
        return tag == null ? null : MapToDto(tag);
    }

    public async Task<TagDetailDto> ChangeStateByMacAsync(string mac, int newStateCode, CancellationToken cancellationToken = default)
    {
        var tag = await _tagService.UpdateTagStateAsync(mac, newStateCode, cancellationToken);

        // Notify background scheduler worker that a new DispatchJob has been persisted in DB
        _dispatchQueue.NotifyJobAvailable();

        _logger.LogInformation("Notified dispatcher of new job for MAC {Mac} (State: {State})", tag.MacAddress, newStateCode);
        return MapToDto(tag);
    }

    public async Task<TagDetailDto> ChangeStateByMachineNoAsync(string machineNo, int newStateCode, CancellationToken cancellationToken = default)
    {
        var tag = await _tagService.GetTagByMachineNoAsync(machineNo, cancellationToken);
        if (tag == null)
        {
            throw new KeyNotFoundException($"No E-Tag linked to machine '{machineNo}'.");
        }

        return await ChangeStateByMacAsync(tag.MacAddress, newStateCode, cancellationToken);
    }

    public async Task<TagDetailDto> LinkTagAsync(LinkTagRequest request, CancellationToken cancellationToken = default)
    {
        var tag = await _tagService.LinkOrUpdateTagAsync(
            request.MacAddress,
            request.MachineNo,
            request.ModelCode,
            request.InitialStateCode,
            cancellationToken);

        if (request.AutoDispatch)
        {
            _dispatchQueue.NotifyJobAvailable();
            _logger.LogInformation("Auto-dispatched newly linked tag {Mac} for Machine {Machine}", tag.MacAddress, tag.MachineNo);
        }

        // Re-fetch to guarantee navigation properties are loaded
        var reloaded = await _tagService.GetTagByMacAsync(tag.MacAddress, cancellationToken) ?? tag;
        return MapToDto(reloaded);
    }

    public async Task<bool> UnlinkTagAsync(string mac, CancellationToken cancellationToken = default)
    {
        return await _tagService.UnlinkTagAsync(mac, cancellationToken);
    }

    private static TagDetailDto MapToDto(EslTag tag)
    {
        return new TagDetailDto(
            MacAddress: tag.MacAddress,
            MachineNo: tag.MachineNo,
            ModelCode: tag.ModelCode,
            StateCode: tag.CurrentStateCode,
            StateNameVi: tag.CurrentState?.StateNameVi ?? $"Trạng thái {tag.CurrentStateCode}",
            StateNameKo: tag.CurrentState?.StateNameKo ?? "",
            ThemeColor: tag.CurrentState?.ThemeColor ?? "Black",
            BatteryLevel: tag.BatteryLevel,
            SyncStatus: tag.SyncStatus,
            LastSyncedAt: tag.LastSyncedAt);
    }
}
