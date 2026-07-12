using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace SentryNet.Services;

public enum ConnProto { Tcp, Tcp6, Udp, Udp6 }

public sealed record NativeConn(
    ConnProto Proto,
    int Pid,
    IPAddress Local,
    int LocalPort,
    IPAddress? Remote,   // null for UDP listeners
    int RemotePort,
    string State);

/// <summary>
/// Enumerates TCP connections and UDP listeners with owning PIDs via iphlpapi,
/// and resolves PID -> process name/path with caching.
/// </summary>
public sealed class ConnectionsService
{
    const int AF_INET = 2;
    const int AF_INET6 = 23;
    const int TCP_TABLE_OWNER_PID_ALL = 5;
    const int UDP_TABLE_OWNER_PID = 1;

    static readonly string[] TcpStates =
    {
        "?", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED",
        "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK",
        "TIME_WAIT", "DELETE_TCB"
    };

    readonly Dictionary<int, (string Name, string? Path)> _procCache = new();
    DateTime _procCacheFlushed = DateTime.UtcNow;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        public uint owningPid;
    }

    static int NetPort(uint dwPort)
    {
        // Port lives in the low 16 bits in network byte order.
        return (int)(((dwPort & 0xFF) << 8) | ((dwPort >> 8) & 0xFF));
    }

    public List<NativeConn> Snapshot()
    {
        var result = new List<NativeConn>(256);
        ReadTable(AF_INET, isTcp: true, result);
        ReadTable(AF_INET6, isTcp: true, result);
        ReadTable(AF_INET, isTcp: false, result);
        ReadTable(AF_INET6, isTcp: false, result);
        return result;
    }

    void ReadTable(int family, bool isTcp, List<NativeConn> result)
    {
        int size = 0;
        // First call gets required buffer size.
        _ = isTcp
            ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UDP_TABLE_OWNER_PID, 0);
        if (size <= 0) return;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            uint err = isTcp
                ? GetExtendedTcpTable(buf, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0)
                : GetExtendedUdpTable(buf, ref size, false, family, UDP_TABLE_OWNER_PID, 0);
            if (err != 0) return;

            int count = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;

            for (int i = 0; i < count; i++)
            {
                if (isTcp && family == AF_INET)
                {
                    var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(row);
                    row += Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                    result.Add(new NativeConn(ConnProto.Tcp, (int)r.owningPid,
                        new IPAddress(r.localAddr), NetPort(r.localPort),
                        new IPAddress(r.remoteAddr), NetPort(r.remotePort),
                        TcpStates[Math.Min(r.state, 12u)]));
                }
                else if (isTcp)
                {
                    var r = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(row);
                    row += Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                    result.Add(new NativeConn(ConnProto.Tcp6, (int)r.owningPid,
                        new IPAddress(r.localAddr, r.localScopeId), NetPort(r.localPort),
                        new IPAddress(r.remoteAddr, r.remoteScopeId), NetPort(r.remotePort),
                        TcpStates[Math.Min(r.state, 12u)]));
                }
                else if (family == AF_INET)
                {
                    var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(row);
                    row += Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                    result.Add(new NativeConn(ConnProto.Udp, (int)r.owningPid,
                        new IPAddress(r.localAddr), NetPort(r.localPort),
                        null, 0, "LISTEN"));
                }
                else
                {
                    var r = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(row);
                    row += Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                    result.Add(new NativeConn(ConnProto.Udp6, (int)r.owningPid,
                        new IPAddress(r.localAddr, r.localScopeId), NetPort(r.localPort),
                        null, 0, "LISTEN"));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public (string Name, string? Path) GetProcessInfo(int pid)
    {
        // Flush the cache occasionally so reused PIDs don't show stale names.
        if ((DateTime.UtcNow - _procCacheFlushed).TotalSeconds > 60)
        {
            _procCache.Clear();
            _procCacheFlushed = DateTime.UtcNow;
        }

        if (_procCache.TryGetValue(pid, out var cached)) return cached;

        (string, string?) info;
        if (pid == 0) info = ("System Idle", null);
        else if (pid == 4) info = ("System", null);
        else
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string? path = null;
                try { path = p.MainModule?.FileName; }
                catch { /* access denied for protected/system processes */ }
                info = (p.ProcessName, path);
            }
            catch
            {
                info = ($"pid {pid} (exited)", null);
            }
        }

        _procCache[pid] = info;
        return info;
    }
}
