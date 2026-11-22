using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MSFSAutoUnpause;

/// <summary>
/// Local ephemeral-port diagnostics. Added 2026-10-25: this machine runs out of ephemeral
/// ports 1-3 times a day (Tcpip events 4231/4266). While that's happening every new outbound
/// connection fails LOCALLY — the internet itself is fine — so a failed network check must
/// not be read as an outage (that could arm auto-unpause on a Pause-at-TOD). See
/// dev-notes.txt "PORT EXHAUSTION IS NO LONGER READ AS AN OUTAGE".
/// </summary>
public static class PortDiagnostics
{
    /// <summary>
    /// True if Windows can't hand out an ephemeral port right now. Tests directly by binding
    /// a TCP and a UDP socket to port 0 (= "give me any ephemeral port") — no packets sent,
    /// nothing leaves the PC. Deliberately doesn't rely on the DNS/TCP check's own error,
    /// since a DNS lookup failing for lack of ports may surface as an ordinary "host not found".
    /// </summary>
    public static bool IsEphemeralPoolExhausted(out string detail)
    {
        string? tcp = TryBindEphemeral(SocketType.Stream, ProtocolType.Tcp);
        string? udp = TryBindEphemeral(SocketType.Dgram,  ProtocolType.Udp);
        detail = (tcp, udp) switch
        {
            (not null, not null) => $"TCP and UDP ({tcp})",
            (not null, null)     => $"TCP ({tcp})",
            (null, not null)     => $"UDP ({udp})",
            _                    => ""
        };
        return tcp != null || udp != null;
    }

    /// <summary>True if this socket error is itself the "out of local ports" signature.</summary>
    public static bool IsExhaustionError(SocketError e) =>
        e == SocketError.NoBufferSpaceAvailable || e == SocketError.AddressAlreadyInUse;

    // Returns null if a port was obtained, otherwise the exhaustion error name. Any OTHER
    // failure is not evidence of exhaustion and returns null.
    private static string? TryBindEphemeral(SocketType st, ProtocolType pt)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, st, pt);
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            return null;
        }
        catch (SocketException ex) when (IsExhaustionError(ex.SocketErrorCode))
        {
            return ex.SocketErrorCode.ToString();
        }
        catch
        {
            return null;
        }
    }

    // ── Port-owner snapshot ──────────────────────────────────────────────────
    // Which processes hold the most sockets, taken AT the moment exhaustion is detected —
    // the trail goes cold within minutes once ports free up, which is why the culprit has
    // never been identified from the Event Log after the fact.

    /// <summary>One-line summary of socket counts per owning process (IPv4+IPv6, TCP+UDP).</summary>
    public static string Snapshot(int top = 8)
    {
        try
        {
            var tcp      = new Dictionary<int, int>();
            var udp      = new Dictionary<int, int>();
            int timeWait = 0, tcpTotal = 0, udpTotal = 0;

            // TCP rows: MIB_TCPROW_OWNER_PID (24 bytes: state@0, pid@20),
            //           MIB_TCP6ROW_OWNER_PID (56 bytes: state@48, pid@52)
            foreach (var (af, size, stateOff, pidOff) in new[] { (AF_INET, 24, 0, 20), (AF_INET6, 56, 48, 52) })
                ForEachRow(true, af, size, row =>
                {
                    int state = Marshal.ReadInt32(row, stateOff);
                    int pid   = Marshal.ReadInt32(row, pidOff);
                    tcpTotal++;
                    if (state == MIB_TCP_STATE_TIME_WAIT) timeWait++;
                    tcp[pid] = tcp.GetValueOrDefault(pid) + 1;
                });

            // UDP rows: MIB_UDPROW_OWNER_PID (12 bytes: pid@8),
            //           MIB_UDP6ROW_OWNER_PID (28 bytes: pid@24)
            foreach (var (af, size, pidOff) in new[] { (AF_INET, 12, 8), (AF_INET6, 28, 24) })
                ForEachRow(false, af, size, row =>
                {
                    int pid = Marshal.ReadInt32(row, pidOff);
                    udpTotal++;
                    udp[pid] = udp.GetValueOrDefault(pid) + 1;
                });

            var topPids = tcp.Keys.Union(udp.Keys)
                .Select(pid => (pid, t: tcp.GetValueOrDefault(pid), u: udp.GetValueOrDefault(pid)))
                .OrderByDescending(x => x.t + x.u)
                .Take(top)
                .Select(x => $"{ProcessName(x.pid)}(pid {x.pid}) tcp={x.t} udp={x.u}");

            return $"TCP={tcpTotal} (TIME_WAIT={timeWait}, these show as pid 0), UDP={udpTotal}; " +
                   $"top owners: {string.Join(", ", topPids)}";
        }
        catch (Exception ex)
        {
            return $"snapshot failed ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string ProcessName(int pid)
    {
        if (pid == 0) return "[none/TIME_WAIT]";
        if (pid == 4) return "System";
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return "[exited]"; }
    }

    private static void ForEachRow(bool isTcp, int af, int rowSize, Action<IntPtr> onRow)
    {
        int    size = 0;
        IntPtr buf  = IntPtr.Zero;
        try
        {
            // Two-call pattern; loop because the table can grow between the calls.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint rc = isTcp
                    ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                    : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
                if (rc == NO_ERROR) break;
                if (rc != ERROR_INSUFFICIENT_BUFFER) return;
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                size += 4096;   // headroom for growth between calls
                buf   = Marshal.AllocHGlobal(size);
                if (attempt == 3) return;
            }
            if (buf == IntPtr.Zero) return;

            int count = Marshal.ReadInt32(buf);   // dwNumEntries, rows follow at offset 4
            for (int i = 0; i < count; i++)
                onRow(buf + 4 + i * rowSize);
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    private const int  AF_INET  = 2;
    private const int  AF_INET6 = 23;
    private const int  TCP_TABLE_OWNER_PID_ALL = 5;
    private const int  UDP_TABLE_OWNER_PID     = 1;
    private const int  MIB_TCP_STATE_TIME_WAIT = 11;
    private const uint NO_ERROR                  = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);
}
