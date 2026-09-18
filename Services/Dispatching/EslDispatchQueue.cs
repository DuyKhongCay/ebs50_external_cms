using System.Threading.Channels;

namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Channel-based high throughput, lock-free queue for tag dispatch jobs.
/// </summary>
internal sealed class EslDispatchQueue : IEslDispatchQueue
{
    private readonly Channel<EslDispatchJob> _channel;
    private readonly Channel<byte> _signalChannel;
    private int _pendingCount;

    public EslDispatchQueue()
    {
        // NOTE: SingleReader option optimizes channel internal lock-free state transitions
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        };

        _channel = Channel.CreateUnbounded<EslDispatchJob>(options);
        _signalChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    public void NotifyJobAvailable()
    {
        _signalChannel.Writer.TryWrite(0);
    }

    public async Task<bool> WaitForJobAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var canRead = await _signalChannel.Reader.WaitToReadAsync(linkedCts.Token);
            if (canRead && _signalChannel.Reader.TryRead(out _))
            {
                return true;
            }
            return canRead;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout reached normally
            return false;
        }
    }

    public async ValueTask EnqueueAsync(EslDispatchJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        Interlocked.Increment(ref _pendingCount);
        await _channel.Writer.WriteAsync(job, cancellationToken);
        NotifyJobAvailable();
    }

    public async IAsyncEnumerable<EslDispatchJob> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _pendingCount);
            yield return job;
        }
    }
}

