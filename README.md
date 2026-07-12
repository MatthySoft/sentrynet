# SentryNet

By **MatthySoft**.

SentryNet is a real-time network monitor for Windows. It reveals every process
making network connections — live down/up rates, resolved hostnames,
geolocation, and ISP owner — and plots each remote endpoint on an interactive
world map. A native WPF app with a dark UI, built on .NET 9.

## Features

- **Connections tab** — every process with network activity: PID, process name,
  connection count, a 60-second down/up rate sparkline (green ↓ / amber ↑,
  updated every 0.5 s), live rates and cumulative totals. Rates are averaged
  over a sliding 3 s window so they read steady instead of flickering.
- **Drilldown** — expand a process row (chevron) to see each remote endpoint:
  protocol, TCP state, resolved hostname, IP, port, geo location, network
  owner (ISP/org), per-endpoint traffic, and **packets received in the last
  60 s** (spot stale connections at a glance).
- **World Map tab** — pan (drag) and zoom (wheel or +/− buttons) like Google
  Maps. Every endpoint is plotted with an always-visible place label; active
  connections pulse with an animated green dash-train along the arc. Country
  names appear as you zoom (small countries stay hidden at world view).
- **Country sidebar** — hideable (☰) list of connections grouped by country;
  click a country to drill into its individual connections.
- **Filter box** — filters by process name, PID, remote IP or hostname.

## Running (development)

```
dotnet build -c Release
.\bin\Release\net9.0-windows\SentryNet.exe
```

## Portable build

```
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

produces `Compiled\SentryNet.exe` — a single self-contained exe (runtime included)
that runs on any 64-bit Windows 10/11 PC with nothing installed. First launch
on a new machine self-extracts to `%TEMP%\.net`, so it takes a few extra seconds
once. Re-run `publish.ps1` after each change set to refresh it.

The app requests elevation on launch (`highestAvailable`). **Run it elevated** —
per-process byte counters come from the ETW kernel network provider, which
requires Administrator. Without elevation everything still works except the
rate/total/packet columns (the header badge tells you which mode you're in).

## How it works

| Piece | Source |
|---|---|
| Connection table | `GetExtendedTcpTable` / `GetExtendedUdpTable` (iphlpapi), polled every 0.5 s |
| Traffic bytes/rates/packets | ETW kernel session, `NetworkTCPIP` keyword (TcpIp/UdpIp send+recv events) via `Microsoft.Diagnostics.Tracing.TraceEvent` |
| Hostnames | Reverse DNS (`Dns.GetHostEntryAsync`), cached in memory |
| Geolocation | `ip-api.com` free batch API (HTTP, non-commercial use, ≤1 batch of 100 IPs per 5 s), cached permanently in `%LOCALAPPDATA%\SentryNet\geocache.json` |
| Map geometry + country labels | Natural Earth 110m countries GeoJSON → `Assets/worldmap.txt` (regenerate with `python tools/convert_geojson.py`) |

## Notes

- Private/LAN addresses are shown but not geolocated (marked "local").
- "flow" rows in the drilldown are endpoints seen only in ETW traffic
  (mostly UDP/QUIC) that have no entry in the TCP connection table.
- Personal use only: the ip-api.com free tier prohibits commercial use.
