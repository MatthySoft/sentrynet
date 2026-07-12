using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace SentryNet.Services;

/// <summary>
/// Sliding ~3 s rate window (7 samples at 0.5 s ticks). Instantaneous per-tick
/// deltas made the UI flicker between a value and "—" for connections that only
/// see a packet every second or two; averaging over 3 s keeps the value steady
/// and it only reads 0 ("—") after a genuinely quiet 3 seconds.
/// </summary>
public sealed class RateWindow
{
    const int N = 7;
    readonly DateTime[] _t = new DateTime[N];
    readonly long[] _a = new long[N];
    readonly long[] _b = new long[N];
    int _head, _count;

    public (double RateA, double RateB) Push(DateTime now, long a, long b)
    {
        _t[_head] = now; _a[_head] = a; _b[_head] = b;
        _head = (_head + 1) % N;
        if (_count < N) _count++;
        int oldest = _count < N ? 0 : _head;
        double secs = (now - _t[oldest]).TotalSeconds;
        if (secs <= 0) return (0, 0);
        return ((a - _a[oldest]) / secs, (b - _b[oldest]) / secs);
    }
}

public sealed class FlowStat
{
    public required IPAddress Remote { get; init; }
    public long Sent;
    public long Recv;
    internal readonly RateWindow Window = new();
    public double SendRate;
    public double RecvRate;
    public DateTime LastSeenUtc = DateTime.UtcNow;

    // Received-packet count over the last 60 s: one bucket per UI tick (0.5 s), 120 buckets.
    internal long RecvPktAccum;
    readonly int[] _pktBuckets = new int[120];
    int _pktHead;
    public long RecvPkts60 { get; private set; }

    internal void RollPktBucket()
    {
        int n = (int)Interlocked.Exchange(ref RecvPktAccum, 0);
        RecvPkts60 += n - _pktBuckets[_pktHead];
        _pktBuckets[_pktHead] = n;
        _pktHead = (_pktHead + 1) % _pktBuckets.Length;
    }
}

public sealed class PidStat
{
    public long Sent;
    public long Recv;
    internal readonly RateWindow Window = new();
    public double SendRate;
    public double RecvRate;
}

/// <summary>
/// Per-process / per-remote-endpoint byte counters from the ETW kernel
/// NetworkTCPIP provider. Needs an elevated process; when not elevated,
/// IsRunning stays false and the app degrades to connection listing.
/// </summary>
public sealed class EtwTrafficService : IDisposable
{
    const string SessionName = "SentryNetKernelSession";

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public readonly record struct FlowKey(int Pid, string RemoteIp, int RemotePort, bool Udp);

    readonly ConcurrentDictionary<FlowKey, FlowStat> _flows = new();
    readonly ConcurrentDictionary<int, PidStat> _pids = new();

    TraceEventSession? _session;
    volatile HashSet<string> _localAddrs = new();
    DateTime _localAddrsRefreshed = DateTime.MinValue;

    public void Start()
    {
        if (!(TraceEventSession.IsElevated() ?? false))
        {
            Error = "Not elevated";
            return;
        }

        try
        {
            RefreshLocalAddresses();

            // Takes over a leftover session with the same name from a previous run.
            _session = new TraceEventSession(SessionName);
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var k = _session.Source.Kernel;
            k.TcpIpSend += d => OnEvent(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, udp: false, sent: true);
            k.TcpIpRecv += d => OnEvent(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, udp: false, sent: false);
            k.TcpIpSendIPV6 += d => OnEvent(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, udp: false, sent: true);
            k.TcpIpRecvIPV6 += d => OnEvent(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, udp: false, sent: false);
            k.UdpIpSend += d => OnEvent(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, udp: true, sent: true);
            k.UdpIpRecv += d => OnEvent(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, udp: true, sent: false);
            k.UdpIpSendIPV6 += d => OnEvent(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, udp: true, sent: true);
            k.UdpIpRecvIPV6 += d => OnEvent(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, udp: true, sent: false);

            var t = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch { /* session stopped */ }
            })
            { IsBackground = true, Name = "EtwPump" };
            t.Start();

            IsRunning = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsRunning = false;
        }
    }

    void RefreshLocalAddresses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    set.Add(ua.Address.ToString());
        }
        catch { }
        _localAddrs = set;
        _localAddrsRefreshed = DateTime.UtcNow;
    }

    void OnEvent(int pid, IPAddress saddr, int sport, IPAddress daddr, int dport, int size, bool udp, bool sent)
    {
        if (pid <= 0 || size <= 0) return;

        // The kernel event carries source and destination of the packet; pick
        // whichever endpoint is NOT one of our local addresses as the remote.
        IPAddress remote;
        int rport;
        var locals = _localAddrs;
        bool dIsLocal = locals.Contains(daddr.ToString());
        bool sIsLocal = locals.Contains(saddr.ToString());
        if (dIsLocal && !sIsLocal) { remote = saddr; rport = sport; }
        else { remote = daddr; rport = dport; }

        var key = new FlowKey(pid, remote.ToString(), rport, udp);
        var flow = _flows.GetOrAdd(key, _ => new FlowStat { Remote = remote });
        var pidStat = _pids.GetOrAdd(pid, _ => new PidStat());

        if (sent)
        {
            Interlocked.Add(ref flow.Sent, size);
            Interlocked.Add(ref pidStat.Sent, size);
        }
        else
        {
            Interlocked.Add(ref flow.Recv, size);
            Interlocked.Add(ref pidStat.Recv, size);
            Interlocked.Increment(ref flow.RecvPktAccum);
        }
        flow.LastSeenUtc = DateTime.UtcNow;
    }

    /// <summary>Compute rates from deltas; call once per UI tick.</summary>
    public void Tick(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return;

        var now = DateTime.UtcNow;
        if ((now - _localAddrsRefreshed).TotalSeconds > 30)
            RefreshLocalAddresses();

        foreach (var (key, f) in _flows)
        {
            long sent = Interlocked.Read(ref f.Sent);
            long recv = Interlocked.Read(ref f.Recv);
            (f.SendRate, f.RecvRate) = f.Window.Push(now, sent, recv);
            f.RollPktBucket();

            if (f.SendRate == 0 && f.RecvRate == 0 &&
                (now - f.LastSeenUtc).TotalSeconds > 300)
                _flows.TryRemove(key, out _);
        }

        foreach (var p in _pids.Values)
        {
            long sent = Interlocked.Read(ref p.Sent);
            long recv = Interlocked.Read(ref p.Recv);
            (p.SendRate, p.RecvRate) = p.Window.Push(now, sent, recv);
        }
    }

    public IReadOnlyDictionary<FlowKey, FlowStat> Flows => _flows;
    public IReadOnlyDictionary<int, PidStat> Pids => _pids;

    public void RemovePid(int pid)
    {
        _pids.TryRemove(pid, out _);
        foreach (var key in _flows.Keys.Where(kk => kk.Pid == pid).ToList())
            _flows.TryRemove(key, out _);
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        IsRunning = false;
    }
}
