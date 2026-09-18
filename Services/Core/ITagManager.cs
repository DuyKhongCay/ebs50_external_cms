using ebs50_backend.DTOs;

namespace ebs50_backend.Services.Core;

/// <summary>
/// Manager layer interface orchestrating business workflows, dispatch queueing, and DTO mappings.
/// </summary>
public interface ITagManager
{
    Task<IReadOnlyList<TagDetailDto>> GetTagsAsync(string? search = null, string? model = null, int? state = null, CancellationToken cancellationToken = default);
    Task<TagDetailDto?> GetTagByMacAsync(string mac, CancellationToken cancellationToken = default);
    Task<TagDetailDto?> GetTagByMachineNoAsync(string machineNo, CancellationToken cancellationToken = default);

    Task<TagDetailDto> ChangeStateByMacAsync(string mac, int newStateCode, CancellationToken cancellationToken = default);
    Task<TagDetailDto> ChangeStateByMachineNoAsync(string machineNo, int newStateCode, CancellationToken cancellationToken = default);

    Task<TagDetailDto> LinkTagAsync(LinkTagRequest request, CancellationToken cancellationToken = default);
    Task<bool> UnlinkTagAsync(string mac, CancellationToken cancellationToken = default);
}

