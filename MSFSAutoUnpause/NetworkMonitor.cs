using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MSFSAutoUnpause;

/// <summary>Snapshot of network health at a point in time.</summary>
public sealed class NetworkStatus
{
    public bool     DnsOk        { get; init; }
    public bool     InternetOk   { get; init; }
    public string?  DnsError     { get; init; }
    public string?  InternetError{ get; init; }
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The check failed, but because THIS PC had run out of ephemeral ports, not because the
    /// network is down — so the result says nothing about the internet either way.
    /// InternetOk is still false (we can't confirm it's up, so never unpause on it), but this
    /// must NOT be treated as an outage: it doesn't stamp LastFailureUtc and never arms a pause.
    /// </summary>
    public bool     Inconclusive { get; init; }
}

/// <summary>
/// Periodically checks DNS resolution and internet connectivity from a
/// background task. LastStatus is updated atomically (reference swap)
/// and safe to read from any thread.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly AppSettings          _cfg;
    private          CancellationTokenSource? _cts;
    private          Task?               _task;

    public NetworkStatus LastStatus { get; private set; } = new NetworkStatus
    {
        DnsOk = true, InternetOk = true     // optimistic initial state
    };

    // When a check last observed the network as DOWN. Read by the pause handler on the UI
    // thread, written here from background/thread-pool threads, so it's stored as a plain
    // long (atomic on x64, unlike a 16-byte Nullable<DateTime> which could tear) behind
    // Volatile read/write rather than locking — it's a timestamp heuristic, not state the
    // app's correctness hinges on. 0 = never failed since startup.
    private long _lastFailureUtcTicks;

    public DateTime? LastFailureUtc
    {
        get
        {
            long t = Volatile.Read(ref _lastFailureUtcTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

    public NetworkMonitor(AppSettings cfg) => _cfg = cfg;

    public void Start()
    {
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => RunLoop(_cts.Token));
        Logger.Info("Network watchdog started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    /// <summary>Run one check immediately (callable from any thread).</summary>
    public async Task<NetworkStatus> CheckNowAsync(CancellationToken ct = default)
    {
        bool dnsOk      = false;
        bool internetOk = false;
        string? dnsErr  = null;
        string? netErr  = null;

        // ── DNS check ──────────────────────────────────────────────────
        try
        {
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(5_000);
            await Dns.GetHostAddressesAsync("self.events.data.microsoft.com", cts2.Token);
            dnsOk = true;
        }
        catch (OperationCanceledException) { dnsErr = "Timeout"; }
        catch (SocketException ex)         { dnsErr = ex.Message; }

        // ── TCP reachability check ────────────────────────────────────
        // DNS resolving is not proof MSFS can actually talk to its services — in particular
        // a cached DNS answer returns success with zero packets sent, so it can hide a real
        // outage; a TCP handshake can't be served from a cache. Only attempt this if DNS
        // succeeded — without a resolved address there's nothing meaningful to connect to.
        bool   tcpOk  = false;
        string? tcpErr = null;
        bool   tcpExhaustionError = false;
        if (dnsOk)
        {
            try
            {
                using var tcpClient = new TcpClient();
                using var cts3 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts3.CancelAfter(5_000);
                await tcpClient.ConnectAsync("self.events.data.microsoft.com", 443, cts3.Token);
                tcpOk = true;
            }
            catch (OperationCanceledException) { tcpErr = "Timeout"; }
            catch (SocketException ex)
            {
                tcpErr = ex.Message;
                tcpExhaustionError = PortDiagnostics.IsExhaustionError(ex.SocketErrorCode);
            }
        }

        // InternetOk requires BOTH DNS resolution and an actual TCP connect to succeed —
        // this is the real "can MSFS reach its services" signal, not just name resolution.
        internetOk = dnsOk && tcpOk;
        netErr     = !dnsOk ? dnsErr : tcpErr;

        // Was this failure actually THIS PC running out of ephemeral ports? (Chronic here,
        // 1-3x/day.) If so the internet may be perfectly fine, and reading it as an outage
        // could auto-unpause a Pause-at-TOD — so the result is "inconclusive", not "down".
        bool   inconclusive   = false;
        string exhaustionInfo = "";
        if (!internetOk)
        {
            inconclusive = PortDiagnostics.IsEphemeralPoolExhausted(out exhaustionInfo) || tcpExhaustionError;
            if (inconclusive && exhaustionInfo.Length == 0) exhaustionInfo = $"TCP connect ({tcpErr})";
        }

        // Stamp every observed REAL failure, not just transitions — the pause handler's grace
        // window needs to know how long ago the network last looked broken, including
        // during a run of consecutive failures. An inconclusive (port-exhaustion) result is
        // deliberately NOT stamped: it must never be able to arm a pause via the grace window.
        if (!internetOk && !inconclusive)
            Volatile.Write(ref _lastFailureUtcTicks, DateTime.UtcNow.Ticks);

        var prev = LastStatus;
        var next = new NetworkStatus
        {
            DnsOk         = dnsOk,
            InternetOk    = internetOk,
            DnsError      = dnsErr,
            InternetError = inconclusive ? $"PC out of local ports — {exhaustionInfo}" : netErr,
            Inconclusive  = inconclusive
        };
        LastStatus = next;   // atomic reference swap

        // Log transitions only. While inconclusive, the DNS/TCP lines are suppressed — they'd
        // read like an outage, which is exactly the misreading this exists to prevent.
        if (inconclusive)
        {
            if (!prev.Inconclusive)
                Logger.Warn($"Network check INCONCLUSIVE — this PC has run out of local ports " +
                            $"({exhaustionInfo}). NOT treated as an outage. (DNS={(dnsOk ? "ok" : dnsErr)}, " +
                            $"TCP={(tcpOk ? "ok" : tcpErr ?? "not tried")})");
            MaybeLogPortSnapshot();
        }
        else
        {
            if (prev.Inconclusive)
                Logger.Info("Local ports available again — network checks conclusive again");

            // Compare against the last CONCLUSIVE reading's meaning: coming out of an
            // inconclusive spell straight into a real failure should still log the failure.
            bool prevDnsOk = prev.Inconclusive || prev.DnsOk;
            bool prevNetOk = prev.Inconclusive || prev.InternetOk;

            if (!dnsOk && prevDnsOk)
                Logger.Warn($"DNS resolution failed: {dnsErr}");
            else if (dnsOk && !prevDnsOk)
                Logger.Info("DNS resolution restored");

            if (dnsOk && !tcpOk && prevNetOk)
                Logger.Warn($"TCP reachability failed despite DNS resolving: {tcpErr}");
            else if (internetOk && !prevNetOk && prev.DnsOk)
                Logger.Info("TCP reachability restored");
        }

        return next;
    }

    // Logs which processes hold the most sockets — at most once every 5 min, so a long
    // exhaustion spell gives a few samples without flooding the log.
    private static readonly TimeSpan PortSnapshotInterval = TimeSpan.FromMinutes(5);
    private long _lastPortSnapshotTicks;

    private void MaybeLogPortSnapshot()
    {
        long now  = DateTime.UtcNow.Ticks;
        long last = Volatile.Read(ref _lastPortSnapshotTicks);
        if (last != 0 && now - last < PortSnapshotInterval.Ticks) return;
        // Several checks can run concurrently (background loop + pause handler); only one
        // of them takes the snapshot.
        if (Interlocked.CompareExchange(ref _lastPortSnapshotTicks, now, last) != last) return;
        Logger.Warn($"Port snapshot (who is holding sockets right now): {PortDiagnostics.Snapshot()}");
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckNowAsync(ct); } catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.NetworkCheckIntervalSecs), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
