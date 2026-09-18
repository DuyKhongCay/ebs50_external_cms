using ebs50_backend.DTOs;

namespace ebs50_backend.Services.Core;

/// <summary>
/// Resilience layer implementing ITagManager with Concurrency Throttling, Circuit Breaker, and Exponential Backoff Retry.
/// Wraps TagManager calls to ensure high availability and prevent overwhelming the SQLite database or EBS-50 RF channel.
/// </summary>
public class ResilientTagManager : ITagManager
{
    private readonly TagManager _innerManager;
    private readonly ILogger<ResilientTagManager> _logger;

    // NOTE: Limit concurrent operations to 10 to prevent SQLite database contention
    private readonly SemaphoreSlim _throttleSemaphore = new(10, 10);

    private static int _consecutiveFailures = 0;
    private static DateTime _circuitOpenUntil = DateTime.MinValue;
    private const int FailureThreshold = 5;
    private static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(30);

    public ResilientTagManager(TagManager innerManager, ILogger<ResilientTagManager> logger)
    {
        _innerManager = innerManager;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TagDetailDto>> GetTagsAsync(
        string? search = null,
        string? model = null,
        int? state = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(
            () => _innerManager.GetTagsAsync(search, model, state, cancellationToken),
            cancellationToken);
    }

    public async Task<TagDetailDto?> GetTagByMacAsync(string mac, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(
            () => _innerManager.GetTagByMacAsync(mac, cancellationToken),
            cancellationToken);
    }

    public async Task<TagDetailDto?> GetTagByMachineNoAsync(string machineNo, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(
            () => _innerManager.GetTagByMachineNoAsync(machineNo, cancellationToken),
            cancellationToken);
    }

    public async Task<TagDetailDto> ChangeStateByMacAsync(string mac, int newStateCode, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(
            () => _innerManager.ChangeStateByMacAsync(mac, newStateCode, cancellationToken),
            cancellationToken);
    }

    public async Task<TagDetailDto> ChangeStateByMachineNoAsync(string machineNo, int newStateCode, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(
            () => _innerManager.ChangeStateByMachineNoAsync(machineNo, newStateCode, cancellationToken),
            cancellationToken);
    }

    public async Task<TagDetailDto> LinkTagAsync(LinkTagRequest request, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(
            () => _innerManager.LinkTagAsync(request, cancellationToken),
            cancellationToken);
    }

    public async Task<bool> UnlinkTagAsync(string mac, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(
            () => _innerManager.UnlinkTagAsync(mac, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Executes the specified asynchronous action protected by Circuit Breaker, Throttling, and Exponential Backoff Retry.
    /// </summary>
    private async Task<T> ExecuteWithResilienceAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _circuitOpenUntil)
        {
            _logger.LogWarning("Circuit breaker is OPEN. Rejecting request until {Until}", _circuitOpenUntil);
            throw new InvalidOperationException($"Hệ thống đang tạm khóa do phát hiện sự cố liên tiếp. Thử lại sau {_circuitOpenUntil.ToLocalTime():HH:mm:ss}.");
        }

        await _throttleSemaphore.WaitAsync(cancellationToken);

        const int maxRetries = 3;
        int delayMs = 150;

        try
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var result = await action();
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return result;
                }
                catch (Exception ex) when (attempt < maxRetries && IsTransient(ex))
                {
                    _logger.LogWarning(ex, "Transient fault encountered on attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms...", attempt, maxRetries, delayMs);
                    await Task.Delay(delayMs, cancellationToken);
                    delayMs *= 2;
                }
                catch (OperationCanceledException)
                {
                    // NOTE: Cancellation requests (user/shutdown) must not trip the failure circuit
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Permanent error encountered in ResilientTagManager.");
                    var failures = Interlocked.Increment(ref _consecutiveFailures);
                    if (failures >= FailureThreshold)
                    {
                        _circuitOpenUntil = DateTime.UtcNow.Add(CircuitBreakDuration);
                        _logger.LogError("Circuit breaker tripped to OPEN state for {Duration}s due to {Failures} consecutive failures.", CircuitBreakDuration.TotalSeconds, failures);
                    }
                    throw;
                }
            }

            throw new TimeoutException("Failed to complete operation after maximum retry attempts.");
        }
        finally
        {
            _throttleSemaphore.Release();
        }
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is TimeoutException
            || ex is System.IO.IOException
            || ex is Microsoft.Data.Sqlite.SqliteException;
    }
}
