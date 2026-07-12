using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SentryNet.Models;
using SentryNet.Services;
using Path = System.Windows.Shapes.Path;

namespace SentryNet;

public partial class MainWindow : Window
{
    const double MapW = 1000, MapH = 500;
    static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.5);

    readonly ConnectionsService _conns = new();
    readonly EtwTrafficService _etw = new();
    readonly DnsResolver _dns = new();
    readonly GeoIpService _geo = new();
    readonly SignatureService _sig = new();
    readonly FirewallService _firewall = new();

    readonly ObservableCollection<ProcessRow> _processes = new();
    readonly Dictionary<int, ProcessRow> _procIndex = new();
    readonly DispatcherTimer _timer;
    DateTime _lastTickUtc = DateTime.UtcNow;

    sealed class MapPoint
    {
        public double Lat, Lon, Rate;
        public long Total;
        public int Conns;
        public readonly SortedSet<string> Procs = new();
        public readonly SortedSet<string> Places = new();
        public readonly SortedSet<string> Hosts = new();
    }

    readonly Dictionary<(double, double), MapPoint> _mapPoints = new();

    // --- map view state (zoom/pan) ---
    double _fitScale = 1;    // scale that fits the whole 1000x500 map in the viewport
    double _relZoom = 1;     // user zoom on top of that (1 = whole world)
    bool _panning;
    Point _panStart;         // mouse position at drag start (viewport coords)
    Point _panOrigin;        // MapPan value at drag start
    string _mapSignature = "";
    // Live overlay dots keyed like _mapPoints, so ticks can update them in place
    // instead of tearing the overlay down (which would close an open tooltip).
    readonly Dictionary<(double, double), (Ellipse Dot, Ellipse? Halo)> _mapDots = new();
    readonly DispatcherTimer _animTimer;
    double _animPhase;
    readonly List<Path> _pulseLines = new();
    readonly List<Ellipse> _pulseHalos = new();

    // --- country sidebar ---
    readonly ObservableCollection<CountryRow> _countries = new();
    readonly Dictionary<string, CountryRow> _countryIndex = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => EnableDarkTitleBar();

        LoadWorldMap();

        ProcGrid.ItemsSource = _processes;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_processes);
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Name), ListSortDirection.Ascending));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(ProcessRow.Name));

        CountryList.ItemsSource = _countries;
        var cview = (ListCollectionView)CollectionViewSource.GetDefaultView(_countries);
        cview.SortDescriptions.Add(new SortDescription(nameof(CountryRow.ConnCount), ListSortDirection.Descending));
        cview.IsLiveSorting = true;
        cview.LiveSortingProperties.Add(nameof(CountryRow.ConnCount));

        _animTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _animTimer.Tick += (_, _) => AnimateMap();

        _etw.Start();
        _geo.Start();

        // Clicking anywhere on a process row toggles its drilldown (handledEventsToo
        // so it fires even though the DataGrid marks the click handled for selection).
        ProcGrid.AddHandler(UIElement.MouseLeftButtonUpEvent,
            new MouseButtonEventHandler(ProcGrid_RowClick), handledEventsToo: true);

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Loaded += (_, _) => OnTick();
        Closed += (_, _) => { _timer.Stop(); _animTimer.Stop(); _etw.Dispose(); _geo.Dispose(); };
    }

    // ================= data merge =================

    void OnTick()
    {
        var nowUtc = DateTime.UtcNow;
        double elapsed = (nowUtc - _lastTickUtc).TotalSeconds;
        _lastTickUtc = nowUtc;

        _etw.Tick(elapsed);

        List<NativeConn> snapshot;
        try { snapshot = _conns.Snapshot(); }
        catch { return; }

        var byPid = new Dictionary<int, List<NativeConn>>();
        foreach (var c in snapshot)
        {
            if (!byPid.TryGetValue(c.Pid, out var list))
                byPid[c.Pid] = list = new List<NativeConn>();
            list.Add(c);
        }
        // Processes only visible through ETW flows (short-lived sockets, UDP).
        foreach (var fk in _etw.Flows.Keys)
            if (!byPid.ContainsKey(fk.Pid))
                byPid[fk.Pid] = new List<NativeConn>();

        _mapPoints.Clear();
        double totalDownRate = 0, totalUpRate = 0;
        long totalDownBytes = 0, totalUpBytes = 0;
        int totalConns = 0;

        foreach (var (pid, list) in byPid)
        {
            if (!_procIndex.TryGetValue(pid, out var row))
            {
                row = new ProcessRow { Pid = pid };
                _procIndex[pid] = row;
                _processes.Add(row);
            }

            var (name, path) = _conns.GetProcessInfo(pid);
            row.Name = name;
            if (row.ExePath != path)
            {
                row.ExePath = path;
                if (path is not null) StartExeChecks(row, path);
            }

            MergeProcess(row, list, nowUtc);

            if (_etw.Pids.TryGetValue(pid, out var ps))
            {
                row.DownRate = ps.RecvRate;
                row.UpRate = ps.SendRate;
                row.TotalDown = Interlocked.Read(ref ps.Recv);
                row.TotalUp = Interlocked.Read(ref ps.Sent);
                totalDownRate += ps.RecvRate;
                totalUpRate += ps.SendRate;
            }
            row.PushHistory(row.DownRate, row.UpRate);

            totalConns += row.ConnCount;
            totalDownBytes += row.TotalDown;
            totalUpBytes += row.TotalUp;
            CollectMapPoints(row, nowUtc);
        }

        // Remove processes that vanished entirely.
        foreach (var pid in _procIndex.Keys.Where(p => !byPid.ContainsKey(p)).ToList())
        {
            _processes.Remove(_procIndex[pid]);
            _procIndex.Remove(pid);
            _etw.RemovePid(pid);
        }

        TotalDownText.Text = _etw.IsRunning ? Fmt.Rate(totalDownRate) : "—";
        TotalUpText.Text = _etw.IsRunning ? Fmt.Rate(totalUpRate) : "—";
        bool showDownBytes = _etw.IsRunning && totalDownBytes > 0;
        bool showUpBytes = _etw.IsRunning && totalUpBytes > 0;
        TotalDownBytesText.Text = showDownBytes ? Fmt.Bytes(totalDownBytes) : "";
        TotalUpBytesText.Text = showUpBytes ? Fmt.Bytes(totalUpBytes) : "";
        DownBytesArrow.Visibility = showDownBytes ? Visibility.Visible : Visibility.Collapsed;
        UpBytesArrow.Visibility = showUpBytes ? Visibility.Visible : Visibility.Collapsed;

        var home = _geo.Home;
        StatusText.Text =
            $"{_processes.Count} Processes · {totalConns} Connections" +
            (home != null ? $" · Home: {home.City}, {home.CountryCode}" : " · Locating…") +
            (_geo.PendingCount > 0 ? $" · Geolocating {_geo.PendingCount} IPs…" : "") +
            (_etw.IsRunning ? "" : " · Traffic stats off — run as Administrator");

        if (Tabs.SelectedIndex == 1)
        {
            BuildCountryList(nowUtc);

            // Rebuild the overlay when the point set changes — but a rebuild tears
            // down every dot, closing any tooltip the user is reading, so while the
            // mouse is over a dot only refresh in place and let the rebuild wait.
            string sig = MapSignature();
            if (sig != _mapSignature && !MapOverlay.IsMouseOver)
            {
                _mapSignature = sig;
                RedrawMap();
            }
            else
            {
                RefreshMapDots();
            }
        }
    }

    void MergeProcess(ProcessRow row, List<NativeConn> conns, DateTime nowUtc)
    {
        // --- rows from the TCP/UDP tables, grouped per remote endpoint ---
        var groups = new Dictionary<string, (NativeConn C, int Count, HashSet<string> States, string Local)>();
        foreach (var c in conns)
        {
            bool listener = c.Remote == null || c.State == "LISTEN" ||
                            (c.RemotePort == 0 && (c.Remote.Equals(IPAddress.Any) || c.Remote.Equals(IPAddress.IPv6Any)));
            string key = listener
                ? $"L|{c.Proto}|{c.LocalPort}"
                : ConnKey(c.Proto is ConnProto.Udp or ConnProto.Udp6, c.Remote!.ToString(), c.RemotePort);

            string local = $"{c.Local}:{c.LocalPort}";
            if (groups.TryGetValue(key, out var g))
            {
                g.States.Add(c.State);
                groups[key] = (g.C, g.Count + 1, g.States, g.Local);
            }
            else
            {
                groups[key] = (c, 1, new HashSet<string> { c.State }, local);
            }
        }

        var seen = new HashSet<string>();

        foreach (var (key, g) in groups)
        {
            seen.Add(key);
            bool isListener = key.StartsWith("L|");
            var remote = isListener ? null : g.C.Remote;

            if (!row.ConnIndex.TryGetValue(key, out var cr))
            {
                cr = new ConnectionRow
                {
                    Key = key,
                    Proto = ProtoLabel(g.C.Proto),
                    LocalEndpoint = g.Local,
                    RemoteAddress = remote,
                    RemoteIp = remote?.ToString() ?? "—",
                    RemotePort = isListener ? 0 : g.C.RemotePort,
                    IsLocal = remote == null || !GeoIpService.IsPublic(remote),
                };
                row.ConnIndex[key] = cr;
                row.Connections.Add(cr);
            }

            cr.State = string.Join("/", g.States.OrderBy(s => s)) + (g.Count > 1 ? $" ×{g.Count}" : "");
            cr.LastSeenUtc = nowUtc;
            // ETW is watching but has never seen a packet for this endpoint.
            if (_etw.IsRunning && cr.Pkts60 == "—") cr.Pkts60 = "0";
            EnrichRow(cr);
        }

        // --- rows/rates from ETW flows ---
        foreach (var (fk, flow) in _etw.Flows)
        {
            if (fk.Pid != row.Pid) continue;
            string key = ConnKey(fk.Udp, fk.RemoteIp, fk.RemotePort);

            if (!row.ConnIndex.TryGetValue(key, out var cr))
            {
                cr = new ConnectionRow
                {
                    Key = key,
                    Proto = (fk.Udp ? "UDP" : "TCP") + (flow.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "v6" : ""),
                    LocalEndpoint = "—",
                    RemoteAddress = flow.Remote,
                    RemoteIp = fk.RemoteIp,
                    RemotePort = fk.RemotePort,
                    State = "flow",
                    IsLocal = !GeoIpService.IsPublic(flow.Remote),
                };
                row.ConnIndex[key] = cr;
                row.Connections.Add(cr);
            }

            EnrichRow(cr); // every tick, so pending DNS/geo lookups fill in when they land
            cr.DownRate = flow.RecvRate;
            cr.UpRate = flow.SendRate;
            cr.TotalDown = Interlocked.Read(ref flow.Recv);
            cr.TotalUp = Interlocked.Read(ref flow.Sent);
            cr.Pkts60 = flow.RecvPkts60.ToString("N0");
            if (seen.Add(key) || flow.RecvRate > 0 || flow.SendRate > 0)
                cr.LastSeenUtc = nowUtc;
        }

        // --- prune rows gone for over a minute ---
        foreach (var cr in row.Connections.Where(c => (nowUtc - c.LastSeenUtc).TotalSeconds > 60).ToList())
        {
            row.Connections.Remove(cr);
            row.ConnIndex.Remove(cr.Key);
        }

        row.ConnCount = row.Connections.Count(c => !c.Key.StartsWith("L|"));
    }

    static string ConnKey(bool udp, string remoteIp, int port) => $"{(udp ? "U" : "T")}|{remoteIp}|{port}";

    static string ProtoLabel(ConnProto p) => p switch
    {
        ConnProto.Tcp => "TCP",
        ConnProto.Tcp6 => "TCPv6",
        ConnProto.Udp => "UDP",
        _ => "UDPv6",
    };

    /// <summary>Fill hostname / geo fields from the async caches (cheap, re-run each tick).</summary>
    void EnrichRow(ConnectionRow cr)
    {
        var ip = cr.RemoteAddress;
        if (ip == null) return;

        if (cr.Hostname is "" or "…")
        {
            var host = _dns.Lookup(ip);
            cr.Hostname = host switch { null => "…", "" => "—", _ => host };
        }

        if (cr.Location == "" && GeoIpService.IsPublic(ip))
        {
            var g = _geo.Lookup(ip);
            if (g != null)
            {
                if (g.Status == "success")
                {
                    cr.Location = string.IsNullOrEmpty(g.City) ? g.Country ?? "?" : $"{g.City}, {g.CountryCode}";
                    cr.Org = g.Org ?? "";
                    cr.Lat = g.Lat;
                    cr.Lon = g.Lon;
                    cr.Country = g.Country ?? "";
                    cr.CountryCode = g.CountryCode ?? "";
                }
                else cr.Location = "—";
            }
        }
        else if (cr.Location == "" && !GeoIpService.IsPublic(ip))
        {
            cr.Location = "local";
        }
    }

    // ================= world map =================

    void LoadWorldMap()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("Assets/worldmap.txt", UriKind.Relative));
            if (res == null) return;

            using var reader = new StreamReader(res.Stream);
            var sb = new StringBuilder(160_000);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // "ISO3|Name|cx,cy,fontsize|path data" — only the path data is drawn.
                var parts = line.Split('|', 4);
                if (parts.Length < 4) continue;
                sb.Append(parts[3]);
            }
            var geom = Geometry.Parse(sb.ToString());
            geom.Freeze();
            WorldPath.Data = geom;
            BorderGlowOuter.Data = geom;
            BorderGlowInner.Data = geom;
            BorderCore.Data = geom;
        }
        catch (Exception ex)
        {
            StatusText.Text = "map data failed to load: " + ex.Message;
        }
    }

    void CollectMapPoints(ProcessRow row, DateTime nowUtc)
    {
        foreach (var cr in row.Connections)
        {
            if (cr.Lat is not double lat || cr.Lon is not double lon) continue;
            if ((nowUtc - cr.LastSeenUtc).TotalSeconds > 60) continue;

            var key = (Math.Round(lat * 2) / 2, Math.Round(lon * 2) / 2);
            if (!_mapPoints.TryGetValue(key, out var mp))
                _mapPoints[key] = mp = new MapPoint { Lat = lat, Lon = lon };

            mp.Rate += cr.DownRate + cr.UpRate;
            mp.Total += cr.TotalDown + cr.TotalUp;
            mp.Conns++;
            mp.Procs.Add(row.Name);
            if (cr.Location.Length > 0 && cr.Location != "—") mp.Places.Add(cr.Location);
            if (cr.Hostname.Length > 1) mp.Hosts.Add(cr.Hostname);
        }
    }

    static Point Project(double lat, double lon) =>
        new((lon + 180.0) / 360.0 * MapW, (90.0 - lat) / 180.0 * MapH);

    /// <summary>Cheap change-detection key for the overlay: point set + activity + counts.</summary>
    string MapSignature()
    {
        var sb = new StringBuilder(_mapPoints.Count * 16);
        foreach (var (k, mp) in _mapPoints.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
            sb.Append(k.Item1).Append(',').Append(k.Item2)
              .Append(mp.Rate > 0 ? 'A' : 'i').Append(mp.Conns).Append(';');
        return sb.ToString();
    }

    // All mark sizes are divided by _relZoom so they keep a constant on-screen
    // size while the geography scales underneath them (Google-Maps style).
    void RedrawMap()
    {
        if (MapOverlay == null) return;
        MapOverlay.Children.Clear();
        _pulseLines.Clear();
        _pulseHalos.Clear();
        _mapDots.Clear();

        var accent = (Brush)FindResource("Accent");
        var down = (Brush)FindResource("Down");
        double z = _relZoom;

        Point? home = null;
        if (_geo.Home is { Lat: double hlat, Lon: double hlon })
            home = Project(hlat, hlon);

        foreach (var (key, mp) in _mapPoints)
        {
            var p = Project(mp.Lat, mp.Lon);
            bool active = mp.Rate > 0;

            if (home is Point h && (Math.Abs(h.X - p.X) > 1 || Math.Abs(h.Y - p.Y) > 1))
            {
                // Slightly curved connector line.
                var mid = new Point((h.X + p.X) / 2, (h.Y + p.Y) / 2);
                var dx = p.X - h.X; var dy = p.Y - h.Y;
                var len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                var ctrl = new Point(mid.X - dy / len * len * 0.15, mid.Y + dx / len * len * 0.15);

                var fig = new PathFigure { StartPoint = h };
                fig.Segments.Add(new QuadraticBezierSegment(ctrl, p, true));
                var pg = new PathGeometry();
                pg.Figures.Add(fig);
                pg.Freeze();

                if (active)
                {
                    // Soft glow underlay + a dash train marching along the arc.
                    MapOverlay.Children.Add(new Path
                    {
                        Data = pg, Stroke = down, StrokeThickness = 4.5 / z,
                        Opacity = 0.16, IsHitTestVisible = false,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    });
                    var line = new Path
                    {
                        Data = pg, Stroke = down, StrokeThickness = 1.5 / z,
                        Opacity = 0.85, IsHitTestVisible = false,
                        StrokeDashArray = new DoubleCollection { 3.2, 4.2 },
                        StrokeDashCap = PenLineCap.Round,
                    };
                    MapOverlay.Children.Add(line);
                    _pulseLines.Add(line);
                }
                else
                {
                    MapOverlay.Children.Add(new Path
                    {
                        Data = pg, Stroke = accent, StrokeThickness = 0.7 / z,
                        Opacity = 0.22, IsHitTestVisible = false,
                    });
                }
            }

            double r = DotRadius(mp, z);

            Ellipse? halo = null;
            if (active)
            {
                // Breathing halo behind the dot (opacity animated in AnimateMap).
                double hr = r * 2.6;
                halo = new Ellipse
                {
                    Width = hr * 2, Height = hr * 2, IsHitTestVisible = false,
                    Fill = new RadialGradientBrush(Color.FromArgb(0x90, 0x3F, 0xB9, 0x50),
                                                   Color.FromArgb(0x00, 0x3F, 0xB9, 0x50)),
                };
                Canvas.SetLeft(halo, p.X - hr);
                Canvas.SetTop(halo, p.Y - hr);
                MapOverlay.Children.Add(halo);
                _pulseHalos.Add(halo);
            }

            var dot = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = active ? down : accent,
                Opacity = 0.9,
                Stroke = Brushes.Black,
                StrokeThickness = 0.5 / z,
            };
            Canvas.SetLeft(dot, p.X - r);
            Canvas.SetTop(dot, p.Y - r);

            // City names live in the tooltip (on-map labels were unreadable).
            dot.ToolTip = DotTip(mp);
            ToolTipService.SetInitialShowDelay(dot, 100);
            ToolTipService.SetShowDuration(dot, int.MaxValue);

            MapOverlay.Children.Add(dot);
            _mapDots[key] = (dot, halo);
        }

        if (home is Point hp)
        {
            var ring = new Ellipse
            {
                Width = 16 / z, Height = 16 / z,
                Stroke = accent, StrokeThickness = 1.5 / z,
                Fill = Brushes.Transparent,
            };
            Canvas.SetLeft(ring, hp.X - 8 / z);
            Canvas.SetTop(ring, hp.Y - 8 / z);
            MapOverlay.Children.Add(ring);

            var dot = new Ellipse { Width = 7 / z, Height = 7 / z, Fill = accent };
            Canvas.SetLeft(dot, hp.X - 3.5 / z);
            Canvas.SetTop(dot, hp.Y - 3.5 / z);
            dot.ToolTip = _geo.Home != null ? $"You — {_geo.Home.City}, {_geo.Home.Country}" : "You";
            ToolTipService.SetShowDuration(dot, int.MaxValue);
            MapOverlay.Children.Add(dot);
        }

        bool animate = Tabs.SelectedIndex == 1 && (_pulseLines.Count > 0 || _pulseHalos.Count > 0);
        if (animate && !_animTimer.IsEnabled) _animTimer.Start();
        else if (!animate && _animTimer.IsEnabled) _animTimer.Stop();
    }

    static double DotRadius(MapPoint mp, double z) =>
        (3.5 + Math.Min(8, Math.Log10(1 + mp.Total / 1024.0) * 1.6)) / z;

    static string DotTip(MapPoint mp)
    {
        var tip = new StringBuilder();
        if (mp.Places.Count > 0)
            tip.AppendLine(string.Join(" · ", mp.Places.Take(3)));
        tip.AppendLine($"{mp.Conns} connection(s) · {Fmt.Rate(mp.Rate)} · {Fmt.Bytes(mp.Total)} total");
        tip.AppendLine("Processes: " + string.Join(", ", mp.Procs.Take(6)));
        if (mp.Hosts.Count > 0)
            tip.Append(string.Join("\n", mp.Hosts.Take(5)));
        return tip.ToString().TrimEnd();
    }

    /// <summary>Per-tick update of dot sizes/positions/tooltips without rebuilding the
    /// overlay, so a tooltip the user is hovering stays open instead of flashing.</summary>
    void RefreshMapDots()
    {
        double z = _relZoom;
        foreach (var (key, mp) in _mapPoints)
        {
            if (!_mapDots.TryGetValue(key, out var v)) continue;
            var p = Project(mp.Lat, mp.Lon);
            double r = DotRadius(mp, z);
            v.Dot.Width = v.Dot.Height = r * 2;
            Canvas.SetLeft(v.Dot, p.X - r);
            Canvas.SetTop(v.Dot, p.Y - r);
            v.Dot.ToolTip = DotTip(mp);
            if (v.Halo is { } halo)
            {
                double hr = r * 2.6;
                halo.Width = halo.Height = hr * 2;
                Canvas.SetLeft(halo, p.X - hr);
                Canvas.SetTop(halo, p.Y - hr);
            }
        }
    }

    /// <summary>~25 fps: march the dash trains outward and breathe the halos.</summary>
    void AnimateMap()
    {
        _animPhase += 0.6;
        foreach (var line in _pulseLines)
            line.StrokeDashOffset = -_animPhase;
        double t = _animPhase * 0.11;
        for (int i = 0; i < _pulseHalos.Count; i++)
            _pulseHalos[i].Opacity = 0.55 + 0.4 * Math.Sin(t + i * 1.7);
    }

    // ================= map zoom / pan =================

    void MapViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double vw = MapViewport.ActualWidth, vh = MapViewport.ActualHeight;
        if (vw < 10 || vh < 10) return;
        _fitScale = Math.Min(vw / MapW, vh / MapH);
        ApplyMapTransform();
    }

    void ApplyMapTransform()
    {
        double scale = _fitScale * _relZoom;
        MapScale.ScaleX = MapScale.ScaleY = scale;
        double cw = MapW * scale, ch = MapH * scale;
        double vw = MapViewport.ActualWidth, vh = MapViewport.ActualHeight;
        // Center when the map is smaller than the viewport, else clamp to its edges.
        MapPan.X = cw <= vw ? (vw - cw) / 2 : Math.Max(vw - cw, Math.Min(0, MapPan.X));
        MapPan.Y = ch <= vh ? (vh - ch) / 2 : Math.Max(vh - ch, Math.Min(0, MapPan.Y));
    }

    /// <summary>Keep the glowing country borders a constant on-screen width while zooming.</summary>
    void UpdateBorderScale()
    {
        double z = _relZoom;
        BorderGlowOuter.StrokeThickness = 3.2 / z;
        BorderGlowInner.StrokeThickness = 1.4 / z;
        BorderCore.StrokeThickness = 0.55 / z;
    }

    void ZoomAt(Point viewportPos, double factor)
    {
        double oldScale = _fitScale * _relZoom;
        _relZoom = Math.Clamp(_relZoom * factor, 1, 60);
        double newScale = _fitScale * _relZoom;
        if (Math.Abs(newScale - oldScale) < 1e-9) return;
        // Keep the map point under the cursor stationary.
        MapPan.X = viewportPos.X - (viewportPos.X - MapPan.X) * (newScale / oldScale);
        MapPan.Y = viewportPos.Y - (viewportPos.Y - MapPan.Y) * (newScale / oldScale);
        ApplyMapTransform();
        UpdateBorderScale();
        RedrawMap(); // marks are counter-scaled, so their size depends on zoom
    }

    void Map_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(MapViewport), e.Delta > 0 ? 1.3 : 1 / 1.3);
        e.Handled = true;
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomAt(new Point(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2), 1.5);

    void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomAt(new Point(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2), 1 / 1.5);

    void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _relZoom = 1;
        ApplyMapTransform();
        UpdateBorderScale();
        RedrawMap();
    }

    void Map_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _panning = true;
        _panStart = e.GetPosition(MapViewport);
        _panOrigin = new Point(MapPan.X, MapPan.Y);
        MapViewport.CaptureMouse();
    }

    void Map_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_panning) return;
        var pos = e.GetPosition(MapViewport);
        MapPan.X = _panOrigin.X + (pos.X - _panStart.X);
        MapPan.Y = _panOrigin.Y + (pos.Y - _panStart.Y);
        ApplyMapTransform();
    }

    void Map_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _panning = false;
        MapViewport.ReleaseMouseCapture();
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // DataGrid row selection bubbles the same routed event — only react to the tabs.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (_animTimer == null) return; // fires during InitializeComponent

        if (Tabs.SelectedIndex == 1)
        {
            BuildCountryList(DateTime.UtcNow);
            _mapSignature = "";
            RedrawMap();
        }
        else
        {
            _animTimer.Stop();
        }
    }

    // ================= country sidebar =================

    void BuildCountryList(DateTime nowUtc)
    {
        // Aggregate live, geolocated connections per country code.
        var agg = new Dictionary<string, (string Name, int Conns, double Rate,
                                          List<(string Key, ProcessRow P, ConnectionRow C)> Items)>();
        foreach (var row in _processes)
        {
            foreach (var cr in row.Connections)
            {
                if (cr.CountryCode.Length == 0) continue;
                if ((nowUtc - cr.LastSeenUtc).TotalSeconds > 60) continue;

                if (!agg.TryGetValue(cr.CountryCode, out var a))
                    a = (cr.Country, 0, 0, new List<(string, ProcessRow, ConnectionRow)>());
                a.Conns++;
                a.Rate += cr.DownRate + cr.UpRate;
                a.Items.Add(($"{row.Pid}|{cr.Key}", row, cr));
                agg[cr.CountryCode] = a;
            }
        }

        foreach (var (code, a) in agg)
        {
            if (!_countryIndex.TryGetValue(code, out var country))
            {
                country = new CountryRow { Code = code };
                _countryIndex[code] = country;
                _countries.Add(country);
            }
            country.Name = a.Name;
            country.ConnCount = a.Conns;
            country.Rate = a.Rate;

            var seen = new HashSet<string>();
            foreach (var (key, p, c) in a.Items.OrderByDescending(t => t.C.DownRate + t.C.UpRate))
            {
                seen.Add(key);
                if (!country.ItemIndex.TryGetValue(key, out var item))
                {
                    item = new CountryConnRow { Key = key };
                    country.ItemIndex[key] = item;
                    country.Items.Add(item);
                }
                item.Process = $"{p.Name}  ({p.Pid})";
                item.Endpoint = c.Hostname.Length > 1 ? c.Hostname : c.RemoteIp;
                item.Detail = $"{c.RemoteIp}:{c.RemotePort} · {c.Location}" +
                              (c.Org.Length > 0 ? $" · {c.Org}" : "");
                item.RateText = $"↓ {Fmt.Rate(c.DownRate)}   ↑ {Fmt.Rate(c.UpRate)}";
            }
            foreach (var stale in country.Items.Where(i => !seen.Contains(i.Key)).ToList())
            {
                country.Items.Remove(stale);
                country.ItemIndex.Remove(stale.Key);
            }
        }

        foreach (var code in _countryIndex.Keys.Where(c => !agg.ContainsKey(c)).ToList())
        {
            _countries.Remove(_countryIndex[code]);
            _countryIndex.Remove(code);
        }
    }

    // ================= UI plumbing =================

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Trim().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyProcFilter();
    }

    void HideLocal_Changed(object sender, RoutedEventArgs e) => ApplyProcFilter();

    /// <summary>Combined process-list filter: text search + (optionally) hide local-only processes.</summary>
    void ApplyProcFilter()
    {
        string text = FilterBox.Text.Trim();
        bool hideLocal = AppState.Instance.HideLocal;
        var view = CollectionViewSource.GetDefaultView(_processes);

        if (text.Length == 0 && !hideLocal) { view.Filter = null; return; }

        view.Filter = o =>
        {
            if (o is not ProcessRow r) return false;
            // Hide processes that only ever talk to local/LAN endpoints.
            if (hideLocal && !r.Connections.Any(c => !c.IsLocal)) return false;
            if (text.Length == 0) return true;
            return r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                   r.Pid.ToString() == text ||
                   r.Connections.Any(c => (!hideLocal || !c.IsLocal) &&
                       (c.RemoteIp.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                        c.Hostname.Contains(text, StringComparison.OrdinalIgnoreCase)));
        };
    }

    // ================= process row click / sidebar =================

    /// <summary>Toggle a process drilldown when its row is clicked (not just the chevron).</summary>
    void ProcGrid_RowClick(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        // Buttons handle their own clicks (chevron toggle, properties button).
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) != null) return;
        // Ignore anything inside the drilldown's nested grid — its rows, column headers,
        // and resize grippers all bubble up to the outer row, so e.g. finishing a
        // column-resize drag there would otherwise collapse the drilldown.
        if (!ReferenceEquals(FindAncestor<DataGrid>(src), ProcGrid)) return;
        if (FindAncestor<DataGridRow>(src)?.Item is ProcessRow pr)
            pr.IsExpanded = !pr.IsExpanded;
    }

    ScrollViewer? _procScroll;

    /// <summary>Scroll the process list by pixels, one fixed step per wheel notch.
    /// Handling this in the tunneling phase means the wheel behaves the same over a
    /// drilldown (whose disabled ScrollViewer would otherwise swallow the event) and
    /// its speed no longer depends on row height.</summary>
    void ProcGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _procScroll ??= FindDescendant<ScrollViewer>(ProcGrid);
        if (_procScroll == null) return;
        e.Handled = true;
        _procScroll.ScrollToVerticalOffset(_procScroll.VerticalOffset - e.Delta);
    }

    void SidebarToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (SidebarCol == null) return; // fires during InitializeComponent
        if (SidebarToggle.IsChecked == true)
        {
            SidebarCol.Width = new GridLength(_sidebarWidth);
        }
        else
        {
            // Remember any user-dragged width, then collapse so no empty gap remains.
            if (SidebarCol.ActualWidth > 10) _sidebarWidth = SidebarCol.ActualWidth;
            SidebarCol.Width = new GridLength(0);
        }
    }

    double _sidebarWidth = 320;

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    static T? FindDescendant<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is T deep) return deep;
        }
        return null;
    }

    void ProcGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Keep live sorting in sync with whatever column the user sorts by.
        Dispatcher.BeginInvoke(() =>
        {
            if (CollectionViewSource.GetDefaultView(_processes) is ListCollectionView v)
            {
                v.LiveSortingProperties.Clear();
                foreach (var sd in v.SortDescriptions)
                    v.LiveSortingProperties.Add(sd.PropertyName);
            }
        }, DispatcherPriority.Background);
    }

    // ================= exe security (signature + firewall) =================

    /// <summary>Kicks off signature and firewall-rule lookups when a row's exe path
    /// first resolves. Signature results usually come from cache; a fresh check runs
    /// on a worker thread and fills in every row sharing that exe when done.</summary>
    void StartExeChecks(ProcessRow row, string exePath)
    {
        row.IsBlocked = _firewall.IsBlocked(exePath);

        bool? trusted = _sig.IsTrusted(exePath, (path, ok) => Dispatcher.BeginInvoke(() =>
        {
            foreach (var pr in _processes)
                if (string.Equals(pr.ExePath, path, StringComparison.OrdinalIgnoreCase))
                    pr.IsUnsigned = !ok;
        }));
        if (trusted is bool t) row.IsUnsigned = !t;
    }

    /// <summary>Toggles the SentryNet outbound-block firewall rule for the row's exe.</summary>
    void Block_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProcessRow row ||
            string.IsNullOrEmpty(row.ExePath)) return;
        string exe = row.ExePath;

        try
        {
            bool block = !row.IsBlocked;
            if (block)
            {
                var pick = MessageBox.Show(this,
                    $"Block all outbound traffic for {row.Name}?\n\n{exe}\n\n" +
                    "This adds a Windows Firewall rule. Click the button again (or use " +
                    "Windows Defender Firewall settings) to remove it.",
                    "SentryNet", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (pick != MessageBoxResult.OK) return;
                _firewall.Block(exe);
            }
            else
            {
                _firewall.Unblock(exe);
            }

            // Multi-process apps share one exe (and one rule) — sync every matching row.
            foreach (var pr in _processes)
                if (string.Equals(pr.ExePath, exe, StringComparison.OrdinalIgnoreCase))
                    pr.IsBlocked = block;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                "Windows denied the firewall change — run SentryNet as Administrator.",
                "SentryNet", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Firewall change failed: " + ex.Message,
                "SentryNet", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================= process properties =================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool ShellExecuteEx(ref ShellExecuteInfo lpExecInfo);

    /// <summary>Opens the Explorer file-properties dialog for the process exe —
    /// the same dialog Task Manager's "Properties" shows.</summary>
    void Properties_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProcessRow row ||
            string.IsNullOrEmpty(row.ExePath)) return;

        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST — run the "properties" shell verb
            hwnd = new WindowInteropHelper(this).Handle,
            lpVerb = "properties",
            lpFile = row.ExePath,
            nShow = 5, // SW_SHOW
        };
        ShellExecuteEx(ref info);
    }

    // ================= dark title bar =================

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { }
    }
}
