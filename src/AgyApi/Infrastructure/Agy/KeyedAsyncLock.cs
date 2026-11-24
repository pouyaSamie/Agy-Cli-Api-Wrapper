namespace AgyApi.Infrastructure.Agy;

/// <summary>
/// Ref-counted keyed async lock that automatically removes and disposes
/// idle semaphores once no callers hold or wait on the key.
/// </summary>
public sealed class KeyedAsyncLock
{
    private sealed class RefCountedSemaphore
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private readonly Dictionary<string, RefCountedSemaphore> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        RefCountedSemaphore entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new RefCountedSemaphore();
                _entries[key] = entry;
            }
            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            DecrementAndCleanup(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    internal int ActiveKeyCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    private void Release(string key, RefCountedSemaphore entry)
    {
        entry.Semaphore.Release();
        DecrementAndCleanup(key, entry);
    }

    private void DecrementAndCleanup(string key, RefCountedSemaphore entry)
    {
        bool shouldDispose = false;
        lock (_sync)
        {
            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                _entries.Remove(key);
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly KeyedAsyncLock _owner;
        private readonly string _key;
        private readonly RefCountedSemaphore _entry;
        private int _disposed;

        public Releaser(KeyedAsyncLock owner, string key, RefCountedSemaphore entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_key, _entry);
            }
        }
    }
}
