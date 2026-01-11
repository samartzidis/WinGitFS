using System.Collections.Concurrent;

namespace WinGitFS;

// Coalesce concurrent requests for the same key into a single Task.
internal sealed class SingleFlight
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> DoAsync<T>(string key, Func<Task<T>> factory) where T : notnull
    {
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await factory().ConfigureAwait(false)));

        try
        {
            var result = await lazy.Value.ConfigureAwait(false);
            return (T)result!;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }
}


