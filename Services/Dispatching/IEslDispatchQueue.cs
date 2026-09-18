namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Immutable job descriptor queued for rendering and dispatching to an ESL tag.
/// </summary>
public sealed record EslDispatchJob(
    string MacAddress,
    string ModelCode,
    int StateCode,
    string? MachineNo = null,
    string? OverrideThemeColor = null);

/// <summary>
/// Asynchronous in-memory channel queue for ESL dispatch jobs.
/// </summary>
public interface IEslDispatchQueue
{
    /// <summary>
    /// Enqueues a dispatch job asynchronously (non-blocking).
    /// </summary>
    ValueTask EnqueueAsync(EslDispatchJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads jobs asynchronously from the queue as an async enumerable stream.
    /// </summary>
    IAsyncEnumerable<EslDispatchJob> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current approximate number of pending items in the queue.
    /// </summary>
    int PendingCount { get; }

    /// <summary>
    /// Notifies the background scheduler that a new job has been saved to the database.
    /// </summary>
    void NotifyJobAvailable();

    /// <summary>
    /// Waits asynchronously until a new job is available or the timeout expires.
    /// </summary>
    Task<bool> WaitForJobAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

