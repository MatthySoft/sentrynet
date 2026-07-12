using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace SentryNet.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>One row in the top-level process table.</summary>
public sealed class ProcessRow : ObservableObject
{
    public int Pid { get; init; }

    string _name = "?";
    public string Name { get => _name; set => Set(ref _name, value); }

    string? _exePath;
    public string? ExePath { get => _exePath; set => Set(ref _exePath, value); }

    int _connCount;
    public int ConnCount { get => _connCount; set => Set(ref _connCount, value); }

    double _downRate;
    public double DownRate { get => _downRate; set => Set(ref _downRate, value); }

    double _upRate;
    public double UpRate { get => _upRate; set => Set(ref _upRate, value); }

    long _totalDown;
    public long TotalDown { get => _totalDown; set => Set(ref _totalDown, value); }

    long _totalUp;
    public long TotalUp { get => _totalUp; set => Set(ref _totalUp, value); }

    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    bool _isUnsigned;
    /// <summary>Exe has no valid Authenticode signature (embedded or catalog).</summary>
    public bool IsUnsigned { get => _isUnsigned; set => Set(ref _isUnsigned, value); }

    bool _isBlocked;
    /// <summary>A SentryNet outbound-block firewall rule exists for this exe.</summary>
    public bool IsBlocked { get => _isBlocked; set => Set(ref _isBlocked, value); }

    // --- 60 s of rate history at 0.5 s ticks, ring buffer read by the Sparkline control ---
    public const int HistoryLen = 120;
    public double[] DownHistory { get; } = new double[HistoryLen];
    public double[] UpHistory { get; } = new double[HistoryLen];
    public int HistoryHead { get; private set; }

    long _historyVersion;
    /// <summary>Bumped once per tick; the Sparkline binds to it to know when to re-render.</summary>
    public long HistoryVersion { get => _historyVersion; private set => Set(ref _historyVersion, value); }

    public void PushHistory(double down, double up)
    {
        DownHistory[HistoryHead] = down;
        UpHistory[HistoryHead] = up;
        HistoryHead = (HistoryHead + 1) % HistoryLen;
        HistoryVersion++;
    }

    public ObservableCollection<ConnectionRow> Connections { get; } = new();

    // Lookup for in-place updates, keyed by ConnectionRow.Key. Not bound to UI.
    public Dictionary<string, ConnectionRow> ConnIndex { get; } = new();
}

/// <summary>One remote endpoint in the per-process drilldown.</summary>
public sealed class ConnectionRow : ObservableObject
{
    public required string Key { get; init; }
    public required string Proto { get; init; }          // "TCP", "TCPv6", "UDP", "UDPv6"
    public required string LocalEndpoint { get; init; }
    public IPAddress? RemoteAddress { get; init; }
    public required string RemoteIp { get; init; }
    public int RemotePort { get; init; }

    /// <summary>Endpoint is local to this machine (loopback / LAN / listener) — used by the hide-local filter.</summary>
    public bool IsLocal { get; init; }

    string _state = "";
    public string State { get => _state; set => Set(ref _state, value); }

    string _hostname = "";
    public string Hostname { get => _hostname; set => Set(ref _hostname, value); }

    string _location = "";
    public string Location { get => _location; set => Set(ref _location, value); }

    string _org = "";
    public string Org { get => _org; set => Set(ref _org, value); }

    string _pkts60 = "—";
    /// <summary>Packets received in the last 60 s ("—" when ETW is off / nothing seen yet).</summary>
    public string Pkts60 { get => _pkts60; set => Set(ref _pkts60, value); }

    // Filled from the geo cache alongside Location; used by the country sidebar.
    public string Country { get; set; } = "";
    public string CountryCode { get; set; } = "";

    double _downRate;
    public double DownRate { get => _downRate; set => Set(ref _downRate, value); }

    double _upRate;
    public double UpRate { get => _upRate; set => Set(ref _upRate, value); }

    long _totalDown;
    public long TotalDown { get => _totalDown; set => Set(ref _totalDown, value); }

    long _totalUp;
    public long TotalUp { get => _totalUp; set => Set(ref _totalUp, value); }

    public double? Lat { get; set; }
    public double? Lon { get; set; }

    /// <summary>Set each merge pass; rows that stay false long enough get removed.</summary>
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One country in the world-map sidebar.</summary>
public sealed class CountryRow : ObservableObject
{
    public required string Code { get; init; }

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    int _connCount;
    public int ConnCount { get => _connCount; set => Set(ref _connCount, value); }

    double _rate;
    public double Rate { get => _rate; set => Set(ref _rate, value); }

    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public ObservableCollection<CountryConnRow> Items { get; } = new();
    public Dictionary<string, CountryConnRow> ItemIndex { get; } = new();
}

/// <summary>One connection inside a country's drilldown in the sidebar.</summary>
public sealed class CountryConnRow : ObservableObject
{
    public required string Key { get; init; }

    string _process = "";
    public string Process { get => _process; set => Set(ref _process, value); }

    string _endpoint = "";
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }

    string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    string _rateText = "";
    public string RateText { get => _rateText; set => Set(ref _rateText, value); }
}

public sealed record GeoInfo(
    string? Status,
    string? Country,
    string? CountryCode,
    string? City,
    double? Lat,
    double? Lon,
    string? Org,
    string? Query);
