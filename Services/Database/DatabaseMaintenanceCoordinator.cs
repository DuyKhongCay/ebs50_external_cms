namespace ebs50_backend.Services.Database;

/// <summary>Drains complete request/worker scopes before replacing the database file.</summary>
public sealed class DatabaseMaintenanceCoordinator
{
    private readonly object _lock = new();
    private int _active;
    private bool _exclusive;
    private bool _faulted;
    private bool _syncPaused;
    private long _generation;
    private TaskCompletionSource? _drained;

    public bool SyncPaused { get { lock (_lock) return _syncPaused; } }
    public bool Unavailable { get { lock (_lock) return _exclusive || _faulted; } }
    public long Generation { get { lock (_lock) return _generation; } }
    public void SetSyncPaused(bool paused) { lock (_lock) _syncPaused = paused; }
    public void MarkRestored() { lock (_lock) _generation++; }
    public void MarkFaulted() { lock (_lock) _faulted = true; }

    public IDisposable? TryEnter(bool worker = false)
    {
        lock (_lock)
        {
            if (_exclusive || _faulted || (worker && _syncPaused)) return null;
            _active++;
            return new Lease(() =>
            {
                lock (_lock)
                {
                    if (--_active == 0) _drained?.TrySetResult();
                }
            });
        }
    }

    public async Task<IDisposable> EnterMaintenanceAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task wait;
        lock (_lock)
        {
            if (_exclusive || _faulted) throw new InvalidOperationException("Database maintenance is unavailable.");
            _exclusive = true;
            _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_active == 0) _drained.TrySetResult();
            wait = _drained.Task;
        }
        try { await wait.WaitAsync(timeout, cancellationToken); }
        catch { ReleaseExclusive(); throw; }
        return new Lease(ReleaseExclusive);
    }

    private void ReleaseExclusive() { lock (_lock) { _exclusive = false; _drained = null; } }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
