using System.Collections.Concurrent;

namespace JobRadar.Sources.Internal;

public sealed class HostRateLimiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequestAt = new();
    private readonly TimeSpan _minInterval;

    public HostRateLimiter(TimeSpan? minInterval = null)
    {
        _minInterval = minInterval ?? TimeSpan.FromSeconds(1);
    }

    public async Task WaitAsync(string host, CancellationToken ct = default)
    {
        var gate = _hostLocks.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_lastRequestAt.TryGetValue(host, out var last))
            {
                var elapsed = DateTimeOffset.UtcNow - last;
                if (elapsed < _minInterval)
                {
                    await Task.Delay(_minInterval - elapsed, ct);
                }
            }
            _lastRequestAt[host] = DateTimeOffset.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }
}
