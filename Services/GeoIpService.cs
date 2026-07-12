using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SentryNet.Models;

namespace SentryNet.Services;

/// <summary>
/// IP geolocation via ip-api.com (free tier, HTTP only, non-commercial use).
/// Batch endpoint: max 100 IPs per POST, max 15 requests/minute — we send at
/// most one batch every 5 seconds. Results are cached on disk permanently.
/// </summary>
public sealed class GeoIpService : IDisposable
{
    static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentryNet");
    static readonly string CacheFile = Path.Combine(CacheDir, "geocache.json");

    const string Fields = "status,country,countryCode,city,lat,lon,org,query";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    readonly ConcurrentDictionary<string, GeoInfo> _cache = new();
    readonly ConcurrentQueue<string> _queue = new();
    readonly ConcurrentDictionary<string, byte> _queued = new();
    readonly CancellationTokenSource _cts = new();

    public GeoInfo? Home { get; private set; }
    public int PendingCount => _queued.Count;

    public void Start()
    {
        LoadCache();
        _ = Task.Run(() => WorkerAsync(_cts.Token));
    }

    /// <summary>Immediate cache lookup; unknown public IPs are queued for the next batch.</summary>
    public GeoInfo? Lookup(IPAddress ip)
    {
        if (!IsPublic(ip)) return null;
        var key = ip.ToString();
        if (_cache.TryGetValue(key, out var info)) return info;
        if (_queued.TryAdd(key, 0)) _queue.Enqueue(key);
        return null;
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] >= 224) return false; // multicast + reserved + broadcast
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any)) return false;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique local
            return true;
        }

        return false;
    }

    async Task WorkerAsync(CancellationToken ct)
    {
        // Own public location first, for the map's "home" marker.
        try
        {
            var json = await _http.GetStringAsync($"http://ip-api.com/json/?fields={Fields}", ct);
            var info = JsonSerializer.Deserialize<GeoInfo>(json, JsonOpts);
            if (info?.Status == "success") Home = info;
        }
        catch { }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = new List<string>(100);
                while (batch.Count < 100 && _queue.TryDequeue(out var ip))
                    batch.Add(ip);

                if (batch.Count > 0)
                {
                    var body = new StringContent(JsonSerializer.Serialize(batch), Encoding.UTF8, "application/json");
                    var resp = await _http.PostAsync($"http://ip-api.com/batch?fields={Fields}", body, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        var results = JsonSerializer.Deserialize<List<GeoInfo>>(json, JsonOpts);
                        if (results != null)
                        {
                            foreach (var r in results)
                            {
                                if (r.Query == null) continue;
                                _cache[r.Query] = r;
                                _queued.TryRemove(r.Query, out _);
                            }
                            SaveCache();
                        }
                    }
                    else
                    {
                        // Rate limited or down: put them back and wait longer.
                        foreach (var ip in batch) _queue.Enqueue(ip);
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, GeoInfo>>(
                File.ReadAllText(CacheFile), JsonOpts);
            if (dict == null) return;
            foreach (var (k, v) in dict) _cache[k] = v;
        }
        catch { }
    }

    void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CacheFile,
                JsonSerializer.Serialize(_cache.ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
