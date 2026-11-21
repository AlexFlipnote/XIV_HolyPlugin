using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HoliestFluffiness.Handlers;

internal sealed class ConnRow
{
    public string Remote = "";
    public int    RemotePort;
    public int    LocalPort;
    public string State = "";
    public string Hint  = "";
    public bool   V6;
    public double InPerSec = double.NaN, OutPerSec = double.NaN;
}

internal sealed class NetSnapshot
{
    public double SysDown, SysUp;                              // bytes/s across active adapters, every process
    public double GameDown = double.NaN, GameUp = double.NaN;  // summed per-connection, needs admin
    public bool   PerConnAvailable;
    public List<ConnRow> Conns = [];
}

// System throughput from the adapters, plus the game's own TCP connections. Windows only hands out
// per-connection byte counts (TCP EStats) to elevated processes; without admin the list has no bytes.
// Purely local reads: no lookups, nothing is sent.
internal sealed class NetworkSampler
{
    private long lastSysIn = -1, lastSysOut = -1;
    private readonly Dictionary<string, (ulong In, ulong Out)> lastConn = new();
    private readonly HashSet<string> enabled = new();
    private bool estatsDenied;

    public NetSnapshot Sample(uint pid, double secs)
    {
        var snap = new NetSnapshot();

        long sysIn = 0, sysOut = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var st = ni.GetIPStatistics();
                sysIn  += st.BytesReceived;
                sysOut += st.BytesSent;
            }
            catch { /* adapter vanished mid-enumeration */ }
        }
        if (lastSysIn >= 0 && secs > 0)
        {
            snap.SysDown = Math.Max(0, (sysIn - lastSysIn) / secs);
            snap.SysUp   = Math.Max(0, (sysOut - lastSysOut) / secs);
        }
        lastSysIn = sysIn; lastSysOut = sysOut;

        var seen = new HashSet<string>();
        double gIn = 0, gOut = 0;
        var any = false;
        foreach (var row in ReadTcp4(pid))
        {
            var c = new ConnRow
            {
                Remote = row.Remote.ToString(), RemotePort = row.RemotePort, LocalPort = row.LocalPort,
                State  = StateName(row.State), Hint = PortHint(row.RemotePort),
            };
            var key = $"{c.Remote}:{c.RemotePort}/{c.LocalPort}";
            seen.Add(key);

            if (!estatsDenied && row.State == 5 && TryEStats(row, key, out var bin, out var bout))
            {
                if (lastConn.TryGetValue(key, out var prev) && secs > 0)
                {
                    c.InPerSec  = (bin - prev.In) / secs;
                    c.OutPerSec = (bout - prev.Out) / secs;
                    gIn += c.InPerSec; gOut += c.OutPerSec; any = true;
                }
                lastConn[key] = (bin, bout);
            }
            snap.Conns.Add(c);
        }
        foreach (var r in ReadTcp6(pid))
        {
            snap.Conns.Add(new ConnRow
            {
                Remote = r.Remote, RemotePort = r.RemotePort, LocalPort = r.LocalPort,
                State  = StateName(r.State), Hint = PortHint(r.RemotePort), V6 = true,
            });
        }

        foreach (var k in lastConn.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            lastConn.Remove(k);
            enabled.Remove(k);
        }

        snap.PerConnAvailable = !estatsDenied;
        if (any) { snap.GameDown = gIn; snap.GameUp = gOut; }
        return snap;
    }

    // FFXIV's published port ranges; 443 is HTTPS from Dalamud, plugins or the launcher
    private static string PortHint(int port) => port switch
    {
        >= 54992 and <= 54994 => "Game (lobby)",
        >= 55006 and <= 55007 => "Game",
        >= 55021 and <= 55040 => "Game",
        443                   => "HTTPS",
        80                    => "HTTP",
        _                     => "",
    };

    private static string StateName(int s) => s switch
    {
        1 => "Closed",   2 => "Listen",   3 => "SynSent",   4 => "SynRcvd", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "DeleteTcb", _ => s.ToString(),
    };

    private unsafe bool TryEStats(Tcp4Row r, string key, out ulong bytesIn, out ulong bytesOut)
    {
        bytesIn = bytesOut = 0;
        var mib = new MIB_TCPROW
        {
            dwState      = (uint)r.State,
            dwLocalAddr  = r.LocalAddrRaw,  dwLocalPort  = r.LocalPortRaw,
            dwRemoteAddr = r.RemoteAddrRaw, dwRemotePort = r.RemotePortRaw,
        };

        if (!enabled.Contains(key))
        {
            byte on = 1;
            var err = SetPerTcpConnectionEStats(ref mib, TcpConnectionEstatsData, &on, 0, 1, 0);
            if (err == 5) { estatsDenied = true; return false; } // ERROR_ACCESS_DENIED: not elevated
            if (err != 0) return false;
            enabled.Add(key);
        }

        // TCP_ESTATS_DATA_ROD_v0 starts with DataBytesOut, DataSegsOut, DataBytesIn (all ULONG64)
        var rod = stackalloc byte[128];
        if (GetPerTcpConnectionEStats(ref mib, TcpConnectionEstatsData, null, 0, 0, null, 0, 0, rod, 0, 128) != 0)
            return false;
        bytesOut = *(ulong*)rod;
        bytesIn  = *(ulong*)(rod + 16);
        return true;
    }

    private readonly record struct Tcp4Row(int State, IPAddress Remote, int RemotePort, int LocalPort,
                                           uint LocalAddrRaw, uint LocalPortRaw, uint RemoteAddrRaw, uint RemotePortRaw);

    // Ports are stored in network byte order in the low 16 bits
    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static List<Tcp4Row> ReadTcp4(uint pid)
    {
        var list = new List<Tcp4Row>();
        var (p, n) = TcpTable(AF_INET);
        if (p == 0) return list;
        try
        {
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 x DWORD)
            for (var i = 0; i < n; i++)
            {
                var row = p + 4 + i * 24;
                if ((uint)Marshal.ReadInt32(row, 20) != pid) continue;
                var st = (uint)Marshal.ReadInt32(row, 0);
                if (st == 2) continue; // listening sockets have no peer
                uint la = (uint)Marshal.ReadInt32(row, 4),  lp = (uint)Marshal.ReadInt32(row, 8);
                uint ra = (uint)Marshal.ReadInt32(row, 12), rp = (uint)Marshal.ReadInt32(row, 16);
                list.Add(new Tcp4Row((int)st, new IPAddress(ra), Port(rp), Port(lp), la, lp, ra, rp));
            }
        }
        finally { Marshal.FreeHGlobal(p); }
        return list;
    }

    private static List<(int State, string Remote, int RemotePort, int LocalPort)> ReadTcp6(uint pid)
    {
        var list = new List<(int, string, int, int)>();
        var (p, n) = TcpTable(AF_INET6);
        if (p == 0) return list;
        try
        {
            // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
            for (var i = 0; i < n; i++)
            {
                var row = p + 4 + i * 56;
                if ((uint)Marshal.ReadInt32(row, 52) != pid) continue;
                var st = Marshal.ReadInt32(row, 48);
                if (st == 2) continue;
                var addr = new byte[16];
                Marshal.Copy(row + 24, addr, 0, 16);
                list.Add((st, new IPAddress(addr).ToString(),
                          Port((uint)Marshal.ReadInt32(row, 44)), Port((uint)Marshal.ReadInt32(row, 20))));
            }
        }
        finally { Marshal.FreeHGlobal(p); }
        return list;
    }

    // Returns the table buffer (caller frees) and its row count, or (0, 0)
    private static (nint Buf, int Rows) TcpTable(int af)
    {
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return (0, 0);
        size += 4096; // the table can grow between the two calls
        var buf = Marshal.AllocHGlobal(size);
        if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
        {
            Marshal.FreeHGlobal(buf);
            return (0, 0);
        }
        return (buf, Marshal.ReadInt32(buf));
    }

    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5, TcpConnectionEstatsData = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW { public uint dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort; }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern unsafe uint SetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, byte* rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern unsafe uint GetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, byte* rw, uint rwVersion, uint rwSize,
                                                                byte* ros, uint rosVersion, uint rosSize, byte* rod, uint rodVersion, uint rodSize);
}
