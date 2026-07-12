using System.Collections.Concurrent;
using System.Net;

namespace SentryNet.Services;

/// <summary>
/// Non-blocking reverse DNS with a permanent in-memory cache.
/// Lookup returns immediately: the hostname if known, "" if resolution
/// failed, or null while a background lookup is still pending.
/// </summary>
public sealed class DnsResolver
{
    readonly ConcurrentDictionary<string, string?> _cache = new();
    readonly ConcurrentDictionary<string, byte> _pending = new();
    readonly SemaphoreSlim _gate = new(8);

    public string? Lookup(IPAddress ip)
    {
        var key = ip.ToString();
        if (_cache.TryGetValue(key, out var name)) return name;

        if (_pending.TryAdd(key, 0))
        {
            _ = ResolveAsync(ip, key);
        }
        return null;
    }

    async Task ResolveAsync(IPAddress ip, string key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var task = Dns.GetHostEntryAsync(ip);
            var done = await Task.WhenAny(task, Task.Delay(4000)).ConfigureAwait(false);
            _cache[key] = done == task ? task.Result.HostName : "";
        }
        catch
        {
            _cache[key] = "";
        }
        finally
        {
            _gate.Release();
            _pending.TryRemove(key, out _);
        }
    }
}
