using ebs50_backend.DTOs;
using ebs50_backend.Models;

namespace ebs50_backend.Services.Core;

/// <summary>
/// Service layer interface handling raw data access and entity persistence for E-Tags, Models, and States.
/// </summary>
public interface ITagService
{
    Task<IReadOnlyList<EslTag>> GetAllTagsAsync(string? search = null, string? modelCode = null, int? stateCode = null, CancellationToken cancellationToken = default);
    Task<EslTag?> GetTagByMacAsync(string mac, CancellationToken cancellationToken = default);
    Task<EslTag?> GetTagByMachineNoAsync(string machineNo, CancellationToken cancellationToken = default);
    Task<EslTag> UpdateTagStateAsync(string mac, int newStateCode, CancellationToken cancellationToken = default);
    Task<EslTag> LinkOrUpdateTagAsync(string mac, string machineNo, string modelCode, int initialStateCode, CancellationToken cancellationToken = default);
    Task<bool> UnlinkTagAsync(string mac, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MachineState>> GetAllStatesAsync(CancellationToken cancellationToken = default);
    Task<MachineState?> GetStateByCodeAsync(int stateCode, CancellationToken cancellationToken = default);
    Task<MachineState> CreateStateAsync(CreateMachineStateRequest request, Stream? iconStream = null, string? iconFileName = null, CancellationToken cancellationToken = default);
    Task<MachineState> UpdateStateAsync(int stateCode, UpdateMachineStateRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteStateAsync(int stateCode, CancellationToken cancellationToken = default);
    Task<string> SaveStateIconAsync(int stateCode, Stream stream, string originalFileName, CancellationToken cancellationToken = default);
    Task<string?> GetStateIconPathAsync(int stateCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelItem>> GetAllModelsAsync(CancellationToken cancellationToken = default);
    Task<ModelItem> AddModelAsync(CreateModelRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteModelAsync(string modelCode, CancellationToken cancellationToken = default);
}

