using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.FlightSimulator.SimConnect;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MSFSAutoUnpause;

internal sealed class MainForm : Form
{
    // ── Win32 helpers (borderless window drag) ────────────────────────────────
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 2;
    private const uint WM_USER_SIMCONNECT = 0x0402;

    // ── Win32 helpers (force-close the MSFS pause menu) ───────────────────────
    // SimConnect's PAUSE_SET event was the original approach — confirmed empirically
    // (1500+ retries over ~5 hours with DNS/internet solidly OK throughout) that MSFS
    // never once honors it once its own Resume/Restart/End Flight menu is showing, so
    // that path was removed entirely. Driving the real UI directly — force focus onto
    // the MSFS window, then Escape (or, failing that, an OCR-located click on Resume) —
    // is what actually closes that menu.
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int  GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern int  GetSystemMetrics(int nIndex);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Canonical Win32 INPUT layout — MUST include MOUSEINPUT and HARDWAREINPUT in the union
    // even though only keyboard/mouse-move+click are used. Omitting them (as the first version
    // of this code did) makes Marshal.SizeOf<INPUT>() return a smaller size than the OS's own
    // INPUT struct on x64, so the cbSize passed to SendInput doesn't match what Windows expects
    // and SendInput rejects every call with ERROR_INVALID_PARAMETER (87) — confirmed exactly
    // this failure empirically (SendInput=0, GetLastError=87) on every retry.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public HARDWAREINPUT Hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    private const int    SW_RESTORE          = 9;
    private const ushort VK_ESCAPE           = 0x1B;
    private const uint   INPUT_KEYBOARD      = 1;
    private const uint   INPUT_MOUSE         = 0;
    private const uint   KEYEVENTF_KEYUP     = 0x0002;
    private const uint   MOUSEEVENTF_MOVE     = 0x0001;
    private const uint   MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint   MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint   MOUSEEVENTF_LEFTUP   = 0x0004;
    private const int    SM_CXSCREEN          = 0;
    private const int    SM_CYSCREEN          = 1;
    private const ushort VK_MENU              = 0x12; // Alt

    // Fraction of the MSFS window's client area where the "Resume" button sits, estimated from
    // observed screenshots of the pause menu (right-anchored button list, vertically centered-ish).
    // Logged with the actual computed pixel coordinates every time it's used — if it's off,
    // the log tells us exactly where it clicked so this can be recalibrated precisely.
    private const double ResumeButtonFracX = 0.86;
    private const double ResumeButtonFracY = 0.385;

    private static readonly string[] MsfsProcessNames = { "FlightSimulator2024", "FlightSimulator" };

    private static IntPtr FindMsfsWindow()
    {
        foreach (var name in MsfsProcessNames)
            foreach (var p in Process.GetProcessesByName(name))
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;

        // Fallback for store/Xbox builds that may report a different process name —
        // scan visible top-level windows for one titled after the sim.
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().IndexOf("Flight Simulator", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Attaches to the thread that owns the *current* foreground window (not the target's
    // thread — that pairing doesn't grant the permission) and logs every return value, since
    // SetForegroundWindow/SendInput fail silently (return false/0) rather than throwing.
    // Also taps Alt right before SetForegroundWindow: Windows' foreground-lock heuristic
    // relaxes when the most recent input was a system key like Alt, which is what made the
    // difference between attempt 1 succeeding vs needing a second attempt in testing.
    // Retries internally (up to 3x, ~100ms apart) so ONE call reliably lands foreground
    // rather than depending on the outer ~10s SendUnpause retry cycle to eventually succeed.
    // Returns whether foreground was actually confirmed — callers MUST check this before
    // sending any input, since SendInput delivers to whatever window currently has focus,
    // not to hWnd specifically; firing blind after a failed steal could hit an unrelated
    // window.
    private static (bool Success, string Log) ForceForegroundWindow(IntPtr hWnd)
    {
        var log = new StringBuilder();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            IntPtr fgWnd = GetForegroundWindow();
            if (fgWnd == hWnd)
            {
                log.Append($"try{attempt}: already foreground");
                return (true, log.ToString());
            }

            uint fgThread = GetWindowThreadProcessId(fgWnd, out _);
            uint myThread = GetCurrentThreadId();
            bool attached = fgThread != myThread && AttachThreadInput(myThread, fgThread, true);

            var altInputs = new INPUT[2];
            altInputs[0] = new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Ki = new KEYBDINPUT { wVk = VK_MENU } } };
            altInputs[1] = new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } };
            SendInput(2, altInputs, Marshal.SizeOf<INPUT>());

            bool fgOk;
            try
            {
                if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
                fgOk = SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }

            System.Threading.Thread.Sleep(100);
            bool nowForeground = GetForegroundWindow() == hWnd;
            log.Append($"try{attempt}: prevFg={fgWnd} attached={attached} SetForegroundWindow={fgOk} nowForeground={nowForeground}; ");

            if (nowForeground) return (true, log.ToString());
        }
        return (false, log.ToString());
    }

    private void TryForceCloseMenu()
    {
        try
        {
            var hWnd = FindMsfsWindow();
            if (hWnd == IntPtr.Zero) { Logger.Warn("Escape fallback: MSFS window not found"); return; }

            var (focused, fgLog) = ForceForegroundWindow(hWnd);
            if (!focused)
            {
                Logger.Warn($"Escape fallback: could not confirm MSFS as the foreground window — " +
                            $"NOT sending Escape this attempt ({fgLog})");
                return;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            Logger.Info($"Escape fallback: {fgLog} pre-send state: pid={pid} SimConnect connected={_simConnected} " +
                        $"stillPaused={_isPaused}");

            var inputs = new INPUT[2];
            inputs[0] = new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Ki = new KEYBDINPUT { wVk = VK_ESCAPE } } };
            inputs[1] = new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Ki = new KEYBDINPUT { wVk = VK_ESCAPE, dwFlags = KEYEVENTF_KEYUP } } };
            uint sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            int  err  = sent == 0 ? Marshal.GetLastWin32Error() : 0;

            Logger.Info($"Escape fallback: SendInput={sent}" + (sent == 0 ? $" (GetLastError={err})" : ""));

            if (sent > 0) SchedulePostActionCheck("Escape", (int)pid);
        }
        catch (Exception ex) { Logger.Warn($"Escape fallback failed: {ex.Message}"); }
    }

    // Moves the real cursor to the "Resume" button and clicks it — a second, independent
    // fallback alongside Escape, since Escape closes the menu via the game's own input
    // handling while this drives the actual UI click MSFS expects from the player.
    // Target position comes from OCR against a live screenshot of the window when possible
    // (survives resolution/layout changes); falls back to a fixed estimated fraction of the
    // window rect only if OCR can't find the text, so a click still fires either way.
    private void TryClickResume()
    {
        try
        {
            var hWnd = FindMsfsWindow();
            if (hWnd == IntPtr.Zero) { Logger.Warn("Mouse fallback: MSFS window not found"); return; }

            var (focused, fgLog) = ForceForegroundWindow(hWnd);
            if (!focused)
            {
                Logger.Warn($"Mouse fallback: could not confirm MSFS as the foreground window — " +
                            $"NOT clicking this attempt ({fgLog})");
                return;
            }

            if (!GetWindowRect(hWnd, out RECT r)) { Logger.Warn("Mouse fallback: GetWindowRect failed"); return; }

            // OCR involves async WinRT calls; run on a pool thread and block here rather than
            // making the whole pause state machine async — this only runs during an already-
            // stuck retry (every ~10s), so a brief synchronous wait is an acceptable trade.
            Point? ocrHit = Task.Run(() => FindResumeButtonViaOcr(r)).GetAwaiter().GetResult();

            int targetX, targetY;
            string how;
            if (ocrHit.HasValue)
            {
                targetX = ocrHit.Value.X;
                targetY = ocrHit.Value.Y;
                how = "ocr";
            }
            else
            {
                targetX = r.Left + (int)((r.Right  - r.Left) * ResumeButtonFracX);
                targetY = r.Top  + (int)((r.Bottom - r.Top)  * ResumeButtonFracY);
                how = "fraction-fallback";
            }

            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            int normX   = (int)(targetX * 65536.0 / screenW);
            int normY   = (int)(targetY * 65536.0 / screenH);

            GetWindowThreadProcessId(hWnd, out uint pid);
            Logger.Info($"Mouse fallback: {fgLog} pre-send state: pid={pid} SimConnect connected={_simConnected} " +
                        $"stillPaused={_isPaused} via={how} clicking=({targetX},{targetY})");

            var inputs = new INPUT[3];
            inputs[0] = new INPUT { Type = INPUT_MOUSE, U = new InputUnion { Mi = new MOUSEINPUT { dx = normX, dy = normY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } } };
            inputs[1] = new INPUT { Type = INPUT_MOUSE, U = new InputUnion { Mi = new MOUSEINPUT { dx = normX, dy = normY, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE } } };
            inputs[2] = new INPUT { Type = INPUT_MOUSE, U = new InputUnion { Mi = new MOUSEINPUT { dx = normX, dy = normY, dwFlags = MOUSEEVENTF_LEFTUP   | MOUSEEVENTF_ABSOLUTE } } };
            uint sent = SendInput(3, inputs, Marshal.SizeOf<INPUT>());
            int  err  = sent == 0 ? Marshal.GetLastWin32Error() : 0;

            Logger.Info($"Mouse fallback: via={how} clicked=({targetX},{targetY}) SendInput={sent}" + (sent == 0 ? $" (GetLastError={err})" : ""));

            if (sent > 0) SchedulePostActionCheck("MouseClick", (int)pid);
        }
        catch (Exception ex) { Logger.Warn($"Mouse fallback failed: {ex.Message}"); }
    }

    // Non-blocking follow-up snapshot taken a couple seconds after an unpause input is sent.
    // Deliberately does NOT block the UI thread (no Thread.Sleep) — this thread also pumps
    // the SimConnect WM_USER message, so blocking it here would delay/risk losing MSFS's own
    // OnRecvQuit notification. Exists to answer one question if MSFS vanishes again right
    // after an unpause attempt: was the process already gone essentially immediately (points
    // at the input itself), or did it linger past this check and quit independently later
    // (points at an MSFS-internal failure during reconnection, unrelated to the keypress)?
    private void SchedulePostActionCheck(string action, int pid)
    {
        var sentAtUtc = DateTime.UtcNow;
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            bool processAlive;
            try { processAlive = !Process.GetProcessById(pid).HasExited; }
            catch { processAlive = false; }

            Logger.Info($"Post-{action} check (+{(DateTime.UtcNow - sentAtUtc).TotalMilliseconds:0}ms): " +
                        $"MSFS process alive={processAlive}, SimConnect connected={_simConnected}, " +
                        $"stillPaused={_isPaused}, foregroundHwnd={GetForegroundWindow()}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // Screenshots the MSFS window and runs Windows' built-in OCR engine over it looking for a
    // line of text reading "Resume". Returns its center in screen coordinates, or null if OCR
    // is unavailable or the text wasn't found — caller falls back to the fixed-fraction guess.
    private static async Task<Point?> FindResumeButtonViaOcr(RECT r)
    {
        try
        {
            int width  = r.Right  - r.Left;
            int height = r.Bottom - r.Top;
            if (width <= 0 || height <= 0) return null;

            using var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(width, height));

            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            using var stream = ms.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Language("en"));
            if (engine == null) { Logger.Warn("OCR: no OCR engine available for any installed language"); return null; }

            var result = await engine.RecognizeAsync(softwareBitmap);
            foreach (var line in result.Lines)
            {
                string text = line.Text.Trim();
                if (!text.Equals("Resume", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Words.Count == 0) continue;

                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var w in line.Words)
                {
                    var wr = w.BoundingRect;
                    minX = Math.Min(minX, wr.X);
                    minY = Math.Min(minY, wr.Y);
                    maxX = Math.Max(maxX, wr.X + wr.Width);
                    maxY = Math.Max(maxY, wr.Y + wr.Height);
                }
                int cx = r.Left + (int)((minX + maxX) / 2);
                int cy = r.Top  + (int)((minY + maxY) / 2);
                Logger.Info($"OCR: found \"Resume\" at local=({minX:F0},{minY:F0})-({maxX:F0},{maxY:F0}) -> screen=({cx},{cy})");
                return new Point(cx, cy);
            }
            Logger.Warn($"OCR: \"Resume\" not found among {result.Lines.Count} recognized line(s)" +
                        (result.Lines.Count > 0 ? ": " + string.Join(" | ", result.Lines.Select(l => l.Text)) : ""));
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"OCR failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ── SimConnect enumerations ───────────────────────────────────────────────
    private enum DataDef    { Flight, Title }
    private enum DataReq    { Flight, Title }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FlightData
    {
        public int    Paused;
        private int   _pad;   // SimConnect pads an INT32 datum to 8 bytes when followed by
                               // FLOAT64 datums in the same definition — without this the
                               // marshaler reads Altitude/GroundSpeed shifted by 4 bytes.
        public double Altitude;
        public double GroundSpeed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct TitleData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
    }

    // ── Design constants ──────────────────────────────────────────────────────
    private static readonly Color C_BG     = Color.FromArgb(221, 225, 231);
    private static readonly Color C_DARK   = Color.FromArgb(155, 164, 180);
    private static readonly Color C_FG     = Color.FromArgb(49,  52,  75);
    private static readonly Color C_ACCENT = Color.FromArgb(74,  144, 217);
    private static readonly Color C_GREEN  = Color.FromArgb(92,  184, 92);
    private static readonly Color C_RED    = Color.FromArgb(224, 92,  92);
    private const int BTN_R = 82;
    private static readonly Point BTN_CENTER = new(190, 205);

    // Hit rects (computed from Width=380)
    private static readonly Rectangle R_CLOSE    = new(380 - 48, 13, 28, 28);
    private static readonly Rectangle R_SETTINGS = new(380 - 82, 13, 28, 28);

    // ── SimConnect state ──────────────────────────────────────────────────────
    private SimConnect?   _sim;
    private bool          _simConnected;
    private bool          _isPaused;
    private DateTime?     _pauseStartUtc;
    private bool          _unpauseSent;
    private DateTime?     _lastUnpauseUtc;
    private string        _aircraftTitle = "Unknown";
    private FlightData    _lastFlight;

    // True once we've confirmed an actual flight is active (not just the main menu).
    // MSFS reports SIMULATION PAUSED=1 on the main menu, identical to a real pause —
    // this flag prevents false auto-unpause triggers before a flight is loaded.
    private bool _flightActive;

    // Network-cause tracking: only auto-unpause if the pause coincides with a
    // network outage. A manual ESC pause will have internet fine; a network-caused
    // pause will have DNS/internet down at or shortly after the pause starts.
    private bool      _pauseNetworkArmed;        // true = network issue confirmed, auto-unpause allowed
    private bool      _pauseNetworkChecked;      // true = initial async check completed
    private DateTime? _lastRestoreCheckUtc;      // last time we polled for internet restoration
    private DateTime? _dnsRestoredUtc;           // when DNS first came back — we wait a stability hold before unpausing
    private DateTime? _dnsStabilityCheckUtc;     // last time we actively re-verified DNS during that hold
    private TimeSpan  _requiredDnsHold = ShortOutageHold; // hold length picked for the current outage (see below)
    private DateTime? _lastUnpauseSentUtc;       // when we last attempted an unpause (for retry tracking)
    private int       _unpauseAttempts;          // how many times we've tried to unpause this session

    // Micropause filter: since SU5, MSFS briefly sets PAUSE STATE bits during engine
    // hitches (asset streaming stalls, autosave, live-weather fetch, ATC/EFB overlays, etc.)
    // — quick enough that the pause screen never actually shows, but observed lasting up to
    // ~4s in practice. Both the full-pause bit (0) and the Active Pause bit (2) are gated:
    // a bit must hold continuously for PauseConfirmDelay before it's treated as real —
    // a shorter blip is ignored entirely (no log entry, no network check, no status change).
    private DateTime? _rawPauseSinceUtc;        // full-pause (bit 0) raw onset
    private DateTime? _rawActivePauseSinceUtc;  // Active Pause (bit 2) raw onset
    private bool      _activePauseConfirmed;    // display-only: bit 2 held long enough to be real
    private static readonly TimeSpan PauseConfirmDelay = TimeSpan.FromSeconds(5);

    // Post-outage stability hold: after a real overnight incident (2026-08-30 — 18-minute
    // DNS outage, Escape sent 6s after DNS came back, MSFS quit 4s later per SimConnect
    // OnRecvQuit — see dev-notes.txt) we can no longer assume 5s is always enough for MSFS
    // to have finished transitioning off the "Connection to network lost" screen. A short
    // blip still gets the old snappy 5s hold; anything at/above LongOutageThreshold gets a
    // longer hold that is also ACTIVELY re-verified (not just trusted from one stale read —
    // see the DNS recheck in ProcessFlight) so a flap during the hold restarts the wait
    // instead of letting a fixed timer expire underneath it.
    private static readonly TimeSpan ShortOutageHold     = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongOutageHold      = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LongOutageThreshold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DnsRecheckInterval  = TimeSpan.FromSeconds(3);

    // Recent-outage grace window: MSFS reacts to a network blip SLOWER than this app's probe
    // recovers from it, so a pause can land after the probe already reads healthy again.
    // Measured on 2026-10-25: network check failed 03:02:29, recovered 03:03:36, and MSFS
    // didn't pause until 03:04:34 — 58s after "restored". A live-only check at pause time
    // reads "fine" there and writes the pause off as intentional (ESC/TOD), which is exactly
    // how a real outage pause ends up sitting untouched all night. So a pause that STARTS
    // within this window of the last observed failure is armed too.
    // Deliberately short (3 min): the cost of making it longer is that a genuine Pause-at-TOD
    // landing soon after an unrelated blip would get auto-unpaused, which is the one outcome
    // this app must never produce. Note this only ever looks BACKWARD from the moment the
    // pause starts — the "never re-arm once a pause is judged intentional" rule is unchanged, so a
    // network drop occurring AFTER an intentional pause still will not arm it.
    private static readonly TimeSpan RecentOutageGrace = TimeSpan.FromMinutes(3);

    // When the network check at pause start is INCONCLUSIVE (this PC out of local ports —
    // see NetworkStatus.Inconclusive), wait this long for a real answer, re-checking at this
    // interval. No answer by then → treated as intentional (never auto-unpaused).
    private static readonly TimeSpan InconclusiveDecisionWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InconclusiveRetryInterval  = TimeSpan.FromSeconds(10);

    // Pump watchdog: added 2026-10-25 after an overnight incident where the app produced
    // zero log output for ~5.5 hours — not even the unconditional "Simulator paused" line —
    // while the process stayed alive and unrestarted the whole time. Everything in this app
    // (pause detection, DNS/TCP checks, auto-unpause) only runs as a side effect of
    // WndProc receiving WM_USER_SIMCONNECT and calling _sim.ReceiveMessage() on the UI
    // thread (see WndProc below) — that single synchronous call has no timeout, so if it
    // ever blocks (leading hypothesis: MSFS wedges after losing its online session and stops
    // servicing the SimConnect pipe — NOT port exhaustion, which is chronic noise on this
    // machine), the entire app goes silent with no crash and no warning. _lastSimDataUtc is stamped on every
    // SimConnect data callback (fires ~1/s via PERIOD.SECOND); a System.Threading.Timer
    // (thread-pool based, NOT a WinForms Timer) checks it independently of the UI message
    // pump, so it keeps ticking even if that pump is the thing that's stuck — a WinForms
    // Timer could not do this, since its own Tick is pumped through the same frozen loop.
    // This is detection-only, not auto-recovery: forcibly tearing down/recreating _sim from
    // a background thread while the UI thread might still be using it is a real thread-safety
    // hazard, and we don't have enough confidence in the exact failure mode yet to justify it.
    private static readonly TimeSpan WatchdogInterval     = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SimDataStaleThreshold = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HeartbeatInterval    = TimeSpan.FromMinutes(5);
    private System.Threading.Timer? _watchdogTimer;
    private DateTime _lastSimDataUtc      = DateTime.UtcNow;
    private DateTime _lastHeartbeatLogUtc = DateTime.UtcNow;
    private bool     _watchdogAlarmActive = false;

    // UI-thread ping (added 2026-10-25, second pass): "no SimConnect data" alone can't tell
    // apart the two failure modes that look identical in the log —
    //   (A) UI thread BLOCKED (e.g. inside ReceiveMessage()) → fix is pumping SimConnect on
    //       its own thread;
    //   (B) UI thread idle and healthy, MSFS simply stopped sending data/WM_USER_SIMCONNECT
    //       → a dedicated pump thread changes nothing; fix is detect-and-reconnect.
    // Each watchdog tick BeginInvokes a no-op onto the UI thread and waits up to
    // UiPingTimeout for it to run. A posted message can only execute if the UI thread is
    // back in its message loop, so "ping answered" ⇒ (B), "ping timed out" ⇒ (A).
    // We also record whether MSFS's own window is responding (Process.Responding —
    // SendMessageTimeout under the hood), which bears on whether MSFS itself is wedged.
    // Blocking a thread-pool thread for up to 5s every 30s is harmless here.
    private static readonly TimeSpan UiPingTimeout = TimeSpan.FromSeconds(5);

    // Status display debounce: MSFS's PAUSE STATE bitmask can flicker for a tick or two
    // during normal flight (Active Pause blips from ATC/EFB overlays, focus-loss micro-pauses,
    // scenery hitches, etc). None of this affects the real unpause logic above — it only
    // smooths what's shown in the UI so the label doesn't flash between states every second.
    private string    _dispStatusText  = "Waiting for MSFS…";
    private Color     _dispStatusColor = Color.FromArgb(130, C_FG);
    private string?   _pendingStatusText;
    private DateTime  _pendingStatusSinceUtc;
    private static readonly TimeSpan StatusDebounce = TimeSpan.FromMilliseconds(1200);

    private readonly AppSettings    _cfg;
    private readonly NetworkMonitor _network;

    // ── Animation state ───────────────────────────────────────────────────────
    private float _animT    = 0f;   // 0=off, 1=on
    private float _pulse    = 0f;   // breathing glow phase
    private float _ripple   = 1f;
    private Color _rippleCol;
    private bool  _btnHover = false;

    // ── Timers ────────────────────────────────────────────────────────────────
    private readonly System.Windows.Forms.Timer _connectTimer;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly System.Windows.Forms.Timer _animTimer;
    private readonly System.Windows.Forms.Timer _pulseTimer;

    // ── Tray ──────────────────────────────────────────────────────────────────
    private NotifyIcon       _tray     = null!;
    private ContextMenuStrip _trayMenu = null!;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        Size            = new Size(380, 555);
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = C_BG;
        DoubleBuffered  = true;
        Text            = "MSFS Auto Unpause";

        // Load app icon (used for taskbar button)
        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        _cfg         = AppSettings.Load();
        _cfg.Enabled = true;
        _network  = new NetworkMonitor(_cfg);
        _animT    = 1f;
        _rippleCol = C_ACCENT;

        _cfg.LastUnpauseUtc        = null;
        _cfg.LastUnpauseAircraft   = null;
        _cfg.LastUnpauseAltitudeFt = 0;
        _cfg.LastUnpausePausedSecs = 0;

        MouseDown  += OnMouseDown;
        MouseMove  += OnMouseMove;
        MouseLeave += (_, _) => { _btnHover = false; Invalidate(); };

        BuildTray();

        _connectTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _connectTimer.Tick += (_, _) => TryConnect();
        _connectTimer.Start();

        // Refresh status text every 500ms
        _uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _uiTimer.Tick += (_, _) => Invalidate();
        _uiTimer.Start();

        // Button toggle animation (~83 fps)
        _animTimer = new System.Windows.Forms.Timer { Interval = 12 };
        _animTimer.Tick += OnAnimTick;

        // Idle breathing glow (~33 fps)
        _pulseTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulse += 0.07f;
            if (_pulse > MathF.PI * 2f) _pulse -= MathF.PI * 2f;
            if (_animT > 0.02f) Invalidate();
        };
        _pulseTimer.Start();

        if (_cfg.NetworkWatchdogEnabled)
            _network.Start();

        // Thread-pool timer, deliberately NOT a WinForms Timer — see the field comment
        // above _watchdogTimer for why it has to be independent of the UI message pump.
        _watchdogTimer = new System.Threading.Timer(WatchdogTick, null, WatchdogInterval, WatchdogInterval);

        Logger.Info($"MSFSAutoUnpause v{Program.Version} started " +
                    $"(.NET {Environment.Version}, {Environment.OSVersion.VersionString})");
        TryConnect();
    }

    // Runs on a thread-pool thread, NOT the UI thread — must stay independent of it to be
    // able to detect a frozen message pump at all. Logger is lock-protected and safe to call
    // from any thread; nothing else touched here needs synchronization for this purpose since
    // it's a best-effort liveness heuristic, not state the app's correctness depends on.
    private void WatchdogTick(object? state)
    {
        try { WatchdogTickCore(); }
        catch (Exception ex) { Logger.Error($"WATCHDOG: tick failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void WatchdogTickCore()
    {
        var  uiPing    = PingUiThread();   // may block this pool thread up to UiPingTimeout
        var  now       = DateTime.UtcNow;
        var  sinceData = now - _lastSimDataUtc;
        bool stale     = _simConnected && sinceData > SimDataStaleThreshold;

        if (stale)
        {
            // Diagnosis of which failure mode this is — see the UiPingTimeout field comment.
            string mode = uiPing == null
                ? "UI thread BLOCKED (ping not answered in " + $"{UiPingTimeout.TotalSeconds:0}s) — consistent with ReceiveMessage() hung"
                : $"UI thread RESPONSIVE (ping {uiPing.Value.TotalMilliseconds:0}ms) — MSFS has stopped sending, app is idle-waiting";
            string diag = $"{mode}; MSFS {DescribeMsfsResponsiveness()}";

            if (!_watchdogAlarmActive)
            {
                _watchdogAlarmActive = true;
                Logger.Error($"WATCHDOG: no SimConnect data received for {sinceData.TotalSeconds:0}s while " +
                             "still marked connected. Pause detection and auto-unpause are NOT running right now. " +
                             diag);
            }
            else
            {
                // Repeat every tick while stuck so the eventual duration — and whether the
                // diagnosis changes over time — is visible in the log without having to infer
                // it from a silent gap, the way 2026-10-25 had to be.
                Logger.Error($"WATCHDOG: still stuck — {sinceData.TotalSeconds:0}s since last SimConnect data. {diag}");
            }
            return;
        }

        if (uiPing == null)
        {
            // Data still fresh but the UI thread didn't answer — a long block somewhere on
            // the UI thread that hasn't (yet) starved SimConnect for 90s. Worth seeing.
            Logger.Warn($"WATCHDOG: UI thread did not answer a ping within {UiPingTimeout.TotalSeconds:0}s " +
                        $"(SimConnect data {sinceData.TotalSeconds:0}s old).");
        }

        if (_watchdogAlarmActive)
        {
            _watchdogAlarmActive = false;
            Logger.Info($"WATCHDOG: SimConnect data flowing again (was stuck for {sinceData.TotalSeconds:0}s).");
        }
        else if (now - _lastHeartbeatLogUtc >= HeartbeatInterval)
        {
            _lastHeartbeatLogUtc = now;
            string ui = uiPing == null ? "timeout" : $"{uiPing.Value.TotalMilliseconds:0}ms";
            Logger.Info($"Heartbeat — monitoring normally (connected={_simConnected}, " +
                        $"flightActive={_flightActive}, paused={_isPaused}, uiPing={ui}).");
        }
    }

    // Posts a no-op to the UI thread and waits for it to run. Returns the round-trip time,
    // or null if it didn't run within UiPingTimeout (UI thread blocked) or couldn't be posted.
    private TimeSpan? PingUiThread()
    {
        if (!IsHandleCreated || IsDisposed) return null;
        using var done = new System.Threading.ManualResetEventSlim(false);
        var sw = Stopwatch.StartNew();
        try
        {
            BeginInvoke(new Action(() => { try { done.Set(); } catch (ObjectDisposedException) { } }));
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            return null;   // handle torn down (app closing)
        }
        return done.Wait(UiPingTimeout) ? sw.Elapsed : null;
    }

    // Is MSFS's own UI thread pumping messages? Process.Responding uses SendMessageTimeout on
    // its main window. Forensic only — tells us whether MSFS itself looked wedged.
    private static string DescribeMsfsResponsiveness()
    {
        try
        {
            foreach (var name in MsfsProcessNames)
            {
                var procs = Process.GetProcessesByName(name);
                try
                {
                    if (procs.Length > 0)
                    {
                        var p = procs[0];
                        return $"pid={p.Id} responding={p.Responding}";
                    }
                }
                finally { foreach (var p in procs) p.Dispose(); }
            }
            return "process not found";
        }
        catch (Exception ex)
        {
            return $"responsiveness check failed ({ex.GetType().Name})";
        }
    }

    // Drop shadow for borderless window
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; }
    }

    // Rounded window corners
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var path = new GraphicsPath();
        const int cr = 18;
        path.AddArc(0, 0, cr * 2, cr * 2, 180, 90);
        path.AddArc(Width - cr * 2, 0, cr * 2, cr * 2, 270, 90);
        path.AddArc(Width - cr * 2, Height - cr * 2, cr * 2, cr * 2, 0, 90);
        path.AddArc(0, Height - cr * 2, cr * 2, cr * 2, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    // SimConnect message pump
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)WM_USER_SIMCONNECT && _sim != null)
        {
            try { _sim.ReceiveMessage(); }
            catch (COMException) { HandleSimDisconnect("WndProc receive error"); }
        }
        base.WndProc(ref m);
    }

    // ── PAINT ─────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(C_BG);

        PaintTitleBar(g);
        PaintButton(g);
        PaintStatus(g);
        PaintLastAction(g);
        PaintBranding(g);
    }

    private void PaintTitleBar(Graphics g)
    {
        using var tf = new Font("Segoe UI", 12f, FontStyle.Bold);
        using var tb = new SolidBrush(C_FG);
        const string title = "MSFS Auto Unpause";
        var tsz = g.MeasureString(title, tf);
        g.DrawString(title, tf, tb, (Width - tsz.Width) / 2f, 18f);

        using var sf = new Font("Segoe UI", 9f);
        using var sb = new SolidBrush(Color.FromArgb(110, C_FG));
        const string sub = "Auto-resumes MSFS after internet interruption";
        var ssz = g.MeasureString(sub, sf);
        g.DrawString(sub, sf, sb, (Width - ssz.Width) / 2f, 43f);

        PaintSmallBtn(g, R_SETTINGS, "⚙");
        PaintSmallBtn(g, R_CLOSE,    "✕");
    }

    private static void PaintSmallBtn(Graphics g, Rectangle r, string icon)
    {
        const int off = 5;
        // Dark shadow (bottom-right)
        using var dp = new GraphicsPath(); dp.AddEllipse(r.X + off, r.Y + off, r.Width + 4, r.Height + 4);
        using var db = new PathGradientBrush(dp);
        db.CenterColor = Color.FromArgb(160, C_DARK); db.SurroundColors = new[] { Color.Transparent };
        g.FillPath(db, dp);

        // Light shadow (top-left)
        using var lp = new GraphicsPath(); lp.AddEllipse(r.X - off, r.Y - off, r.Width + 4, r.Height + 4);
        using var lb = new PathGradientBrush(lp);
        lb.CenterColor = Color.FromArgb(255, Color.White); lb.SurroundColors = new[] { Color.Transparent };
        g.FillPath(lb, lp);

        // Face
        using var fb = new SolidBrush(C_BG);
        g.FillEllipse(fb, r);

        // Subtle border
        using var border = new Pen(Color.FromArgb(40, C_DARK), 1f);
        g.DrawEllipse(border, r);

        // Icon
        using var f = new Font("Segoe UI", 10f);
        using var b = new SolidBrush(Color.FromArgb(190, C_FG));
        var sz = g.MeasureString(icon, f);
        g.DrawString(icon, f, b, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f - 1f);
    }

    private void PaintButton(Graphics g)
    {
        int   cx = BTN_CENTER.X, cy = BTN_CENTER.Y, r = BTN_R;
        float te  = Ease(_animT);
        int   sOff = (int)(14f * (1f - 2f * te));   // +14 raised → -14 inset

        // Dark shadow — larger spread, higher alpha for prominent depth
        using var dkPath = new GraphicsPath();
        dkPath.AddEllipse(cx - r - 22 + sOff, cy - r - 22 + sOff, (r + 22) * 2, (r + 22) * 2);
        using var dkBrush = new PathGradientBrush(dkPath);
        dkBrush.CenterColor = Color.FromArgb(185, C_DARK); dkBrush.SurroundColors = new[] { Color.Transparent };
        g.FillPath(dkBrush, dkPath);

        // Light shadow — pure white, wide spread
        using var ltPath = new GraphicsPath();
        ltPath.AddEllipse(cx - r - 22 - sOff, cy - r - 22 - sOff, (r + 22) * 2, (r + 22) * 2);
        using var ltBrush = new PathGradientBrush(ltPath);
        ltBrush.CenterColor = Color.FromArgb(255, Color.White); ltBrush.SurroundColors = new[] { Color.Transparent };
        g.FillPath(ltBrush, ltPath);

        // Subtle hard border ring for crisp edge definition
        using var edgePen = new Pen(Color.FromArgb(45, C_DARK), 1.5f);
        g.DrawEllipse(edgePen, cx - r, cy - r, r * 2, r * 2);

        // Breathing glow rings (ON state)
        if (te > 0.02f)
        {
            float breath = MathF.Sin(_pulse) * 0.5f + 0.5f;
            int[]   adds = { 14, 25, 38 };
            float[] base_ = { 0.44f, 0.21f, 0.09f };
            for (int i = 0; i < 3; i++)
            {
                float ra = r + adds[i] + 3f * breath * te;
                float al = base_[i] * te * (0.55f + 0.45f * breath);
                using var gp = new GraphicsPath();
                gp.AddEllipse(cx - ra, cy - ra, ra * 2, ra * 2);
                using var gb2 = new PathGradientBrush(gp);
                gb2.CenterColor = Color.FromArgb((int)(al * 195), C_ACCENT);
                gb2.SurroundColors = new[] { Color.Transparent };
                g.FillPath(gb2, gp);
            }
        }

        // Main circle
        using var circleBrush = new SolidBrush(LerpColor(C_BG, C_ACCENT, te * 0.38f));
        g.FillEllipse(circleBrush, cx - r, cy - r, r * 2, r * 2);

        // Inner highlight rim
        if (te > 0.05f)
        {
            using var rimPen = new Pen(Color.FromArgb((int)(130 * te), Color.White), 2f);
            g.DrawEllipse(rimPen, cx - r + 5, cy - r + 5, (r - 5) * 2, (r - 5) * 2);
        }

        // Power symbol — arc with 60° gap at top (120° start, 300° sweep), plus vertical stem
        int sr = (int)(r * 0.52f);
        using var symPen = new Pen(LerpColor(Color.FromArgb(110, C_FG), Color.White, te), 3.5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(symPen, cx - sr, cy - sr, sr * 2, sr * 2, 120f, 300f);
        g.DrawLine(symPen, cx, cy - sr + 2, cx, cy - sr / 2 + 2);

        // Click ripple
        if (_ripple < 0.97f)
        {
            float rr = r + 45f * _ripple;
            float al = (1f - _ripple) * 0.55f;
            using var rp = new Pen(Color.FromArgb((int)(al * 255), _rippleCol), Math.Max(1, (int)(3.5f * (1 - _ripple))));
            g.DrawEllipse(rp, cx - rr, cy - rr, rr * 2, rr * 2);
        }

        // ON / OFF label beneath button
        using var lblFont  = new Font("Segoe UI", 11f, FontStyle.Bold);
        string lbl = _cfg.Enabled ? "ON" : "OFF";
        var lblCol = _cfg.Enabled ? LerpColor(Color.FromArgb(100, C_FG), C_ACCENT, te) : Color.FromArgb(120, C_FG);
        using var lblBrush = new SolidBrush(lblCol);
        var lblSz  = g.MeasureString(lbl, lblFont);
        g.DrawString(lbl, lblFont, lblBrush, cx - lblSz.Width / 2f, cy + r + 14f);
    }

    private void PaintStatus(Graphics g)
    {
        int y  = BTN_CENTER.Y + BTN_R + 52;
        int lx = 55, dotX = 188, vx = 202;

        using var lf = new Font("Segoe UI", 9.5f);
        using var vf = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var lb = new SolidBrush(Color.FromArgb(125, C_FG));

        void Row(int ry, string label, string value, Color vc)
        {
            g.DrawString(label, lf, lb, lx, ry);
            using var dot = new SolidBrush(vc);
            g.FillEllipse(dot, dotX, ry + 5, 7, 7);
            using var vb  = new SolidBrush(vc);
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(value, vf, vb, new RectangleF(vx, ry, Width - vx - 10, vf.Height + 4), fmt);
        }

        var net = _network.LastStatus;
        Row(y,      "Internet", net.InternetOk ? "Connected" : net.Inconclusive ? "Unknown (no local ports)" : "Disconnected",
                                net.InternetOk ? C_GREEN : C_RED);
        Row(y + 28, "MSFS",     _simConnected  ? "Connected"  : "Not found",    _simConnected  ? C_GREEN : Color.FromArgb(130, C_FG));

        string stxt; Color scol;
        if (!_cfg.Enabled)              { stxt = "Disabled";             scol = Color.FromArgb(130, C_FG); }
        else if (!_simConnected)        { stxt = "Waiting for MSFS…";   scol = Color.FromArgb(130, C_FG); }
        else if (!_flightActive)        { stxt = "Waiting for flight…"; scol = Color.FromArgb(130, C_FG); }
        else if (_isPaused)
        {
            double sec = _pauseStartUtc.HasValue ? (DateTime.UtcNow - _pauseStartUtc.Value).TotalSeconds : 0;
            int    rem = Math.Max(0, _cfg.PauseThresholdSeconds - (int)sec);
            if (_unpauseSent)
                stxt = _unpauseAttempts > 1 ? $"Unpausing… (attempt {_unpauseAttempts})" : "Unpausing…";
            else if (!_pauseNetworkChecked)
                stxt = "Paused · checking…";
            else if (!_pauseNetworkArmed)
                stxt = "Paused · manual";
            else if (rem > 0)
                stxt = $"Paused {sec:0}s · {rem}s left";
            else if (_dnsRestoredUtc.HasValue)
            {
                int hold = Math.Max(0, (int)_requiredDnsHold.TotalSeconds - (int)(DateTime.UtcNow - _dnsRestoredUtc.Value).TotalSeconds);
                stxt = hold > 0 ? $"Reconnected · resuming in {hold}s…" : "Unpausing…";
            }
            else
                stxt = "Waiting to reconnect…";
            scol = C_RED;
        }
        else if (_activePauseConfirmed)
        {
            stxt = "Active pause";
            scol = Color.FromArgb(210, 150, 40);
        }
        else { stxt = "Monitoring"; scol = C_GREEN; }

        // Debounce: only commit a status change to the display once the freshly-computed
        // value has held steady for StatusDebounce, so a one-tick blip in MSFS's PAUSE STATE
        // (Active Pause overlays, focus-loss micro-pauses, etc) doesn't flash the label.
        // Purely cosmetic — the real pause/unpause state machine above is unaffected.
        if (stxt == _dispStatusText)
        {
            _pendingStatusText = null;
        }
        else if (stxt != _pendingStatusText)
        {
            _pendingStatusText    = stxt;
            _pendingStatusSinceUtc = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - _pendingStatusSinceUtc >= StatusDebounce)
        {
            _dispStatusText     = stxt;
            _dispStatusColor    = scol;
            _pendingStatusText  = null;
        }

        Row(y + 56, "Status", _dispStatusText, _dispStatusColor);
    }

    private void PaintLastAction(Graphics g)
    {
        int cardY = BTN_CENTER.Y + BTN_R + 52 + 90;  // just below the 3 status rows
        var card  = new Rectangle(24, cardY, Width - 48, 72);

        bool hasEvent = _cfg.LastUnpauseUtc.HasValue;

        // Inset card shadow (reversed = sunken effect)
        using var dkPath  = RoundedRect(new Rectangle(card.X - 2, card.Y - 2, card.Width + 4, card.Height + 4), 10);
        using var dkBrush = new PathGradientBrush(dkPath);
        dkBrush.CenterColor    = Color.FromArgb(hasEvent ? 90 : 50, C_DARK);
        dkBrush.SurroundColors = new[] { Color.Transparent };
        g.FillPath(dkBrush, dkPath);

        using var ltPath  = RoundedRect(new Rectangle(card.X + 2, card.Y + 2, card.Width + 4, card.Height + 4), 10);
        using var ltBrush = new PathGradientBrush(ltPath);
        ltBrush.CenterColor    = Color.FromArgb(hasEvent ? 200 : 120, Color.White);
        ltBrush.SurroundColors = new[] { Color.Transparent };
        g.FillPath(ltBrush, ltPath);

        // Card face — tinted blue if an unpause happened
        var faceCol = hasEvent ? Color.FromArgb(12, C_ACCENT) : Color.FromArgb(6, C_FG);
        using var faceBrush = new SolidBrush(faceCol);
        using var facePath  = RoundedRect(card, 10);
        g.FillPath(faceBrush, facePath);

        // Card border
        using var borderPen = new Pen(Color.FromArgb(hasEvent ? 35 : 20, C_ACCENT), 1f);
        g.DrawPath(borderPen, facePath);

        int tx = card.X + 14, ty = card.Y + 10;

        if (!hasEvent)
        {
            using var dimFont = new Font("Segoe UI", 9f);
            using var dimBrush = new SolidBrush(Color.FromArgb(90, C_FG));
            g.DrawString("No auto-unpause events yet", dimFont, dimBrush, tx, ty + 20);
            return;
        }

        // Header row: checkmark + "Auto-unpaused"
        using var hf = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var hb = new SolidBrush(C_GREEN);
        g.DrawString("✓  Auto-unpaused", hf, hb, tx, ty);

        // Timestamp (right-aligned)
        string timeStr = _cfg.LastUnpauseUtc!.Value.ToLocalTime().ToString("h:mm tt  d MMM");
        using var tf2 = new Font("Segoe UI", 9f);
        var tSz = g.MeasureString(timeStr, tf2);
        using var tb2 = new SolidBrush(Color.FromArgb(150, C_FG));
        g.DrawString(timeStr, tf2, tb2, card.Right - tSz.Width - 14, ty + 1);

        // Detail row: aircraft + altitude
        string ac     = _cfg.LastUnpauseAircraft ?? "Unknown aircraft";
        if (ac.Length > 32) ac = ac[..32] + "…";
        string alt    = $"{_cfg.LastUnpauseAltitudeFt:N0} ft";
        string detail = $"{ac}  ·  {alt}";

        using var df = new Font("Segoe UI", 9f);
        using var db2 = new SolidBrush(Color.FromArgb(160, C_FG));
        g.DrawString(detail, df, db2, tx, ty + 24);
    }

    private void PaintBranding(Graphics g)
    {
        using var f = new Font("Segoe UI", 8.5f);
        using var b = new SolidBrush(Color.FromArgb(85, C_FG));
        string txt = $"Made by ItzChickenYall  ·  v{Program.Version}";
        var sz = g.MeasureString(txt, f);
        g.DrawString(txt, f, b, (Width - sz.Width) / 2f, Height - 22f);
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────
    private bool HitBtn(Point p)
    {
        int dx = p.X - BTN_CENTER.X, dy = p.Y - BTN_CENTER.Y;
        return dx * dx + dy * dy <= BTN_R * BTN_R;
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (R_CLOSE.Contains(e.Location))    { Hide(); return; }
        if (R_SETTINGS.Contains(e.Location)) { OpenSettings(); return; }

        if (HitBtn(e.Location))
        {
            _cfg.Enabled = !_cfg.Enabled;
            _cfg.Save();
            _ripple    = 0f;
            _rippleCol = _cfg.Enabled ? C_ACCENT : C_RED;
            Logger.Info($"Auto-unpause {(_cfg.Enabled ? "enabled" : "disabled")} by user");
            _animTimer.Start();
            return;
        }

        // Drag anywhere else
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        bool was = _btnHover;
        _btnHover = HitBtn(e.Location);
        Cursor = (_btnHover || R_CLOSE.Contains(e.Location) || R_SETTINGS.Contains(e.Location))
            ? Cursors.Hand : Cursors.Default;
        if (was != _btnHover) Invalidate();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ── Animation ─────────────────────────────────────────────────────────────
    private static float Ease(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        float target = _cfg.Enabled ? 1f : 0f;
        const float step = 1f / 22f;
        if (Math.Abs(_animT - target) <= step) { _animT = target; _animTimer.Stop(); }
        else _animT += _cfg.Enabled ? step : -step;
        if (_ripple < 1f) _ripple = Math.Min(1f, _ripple + 0.05f);
        Invalidate();
    }

    // ── Settings ──────────────────────────────────────────────────────────────
    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_cfg);
        dlg.ShowDialog(this);
        if (_cfg.NetworkWatchdogEnabled)
            _network.Start();
        Invalidate();
    }

    // ── Tray (minimize-to-tray only) ──────────────────────────────────────────
    private void BuildTray()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Show",  null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        _trayMenu.Items.Add("Exit",  null, (_, _) => { Logger.Info("Exiting"); Application.Exit(); });

        string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        var trayIcon = File.Exists(iconPath)
            ? new Icon(iconPath, 32, 32)
            : MakeTrayIcon(Color.FromArgb(60, 140, 220));

        _tray = new NotifyIcon
        {
            ContextMenuStrip = _trayMenu,
            Icon             = trayIcon,
            Text             = "MSFS Auto Unpause",
            Visible          = true
        };
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    // ── SimConnect ────────────────────────────────────────────────────────────
    private void TryConnect()
    {
        if (_simConnected) return;
        try
        {
            _sim = new SimConnect("MSFSAutoUnpause", Handle, WM_USER_SIMCONNECT, null, 0);
            _sim.OnRecvOpen                += OnSimOpen;
            _sim.OnRecvQuit                += OnSimQuit;
            _sim.OnRecvException           += OnSimException;
            _sim.OnRecvSimobjectData       += OnSimObjectData;
            _sim.OnRecvSimobjectDataBytype += OnSimObjectDataByType;
        }
        catch (COMException) { _sim = null; }
        catch (Exception ex) { Logger.Warn($"SimConnect unavailable: {ex.GetType().Name}: {ex.Message}"); _sim = null; }
    }

    private void OnSimOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        _simConnected = true;
        Logger.Info($"Connected to MSFS · {data.szApplicationName} " +
                    $"v{data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor}");
        try
        {
            sender.AddToDataDefinition(DataDef.Flight, "PAUSE STATE",       null,   SIMCONNECT_DATATYPE.INT32,   0f, SimConnect.SIMCONNECT_UNUSED);
            sender.AddToDataDefinition(DataDef.Flight, "PLANE ALTITUDE",    "feet",  SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sender.AddToDataDefinition(DataDef.Flight, "GROUND VELOCITY",   "knots", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
            sender.RegisterDataDefineStruct<FlightData>(DataDef.Flight);

            sender.AddToDataDefinition(DataDef.Title, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
            sender.RegisterDataDefineStruct<TitleData>(DataDef.Title);

            sender.RequestDataOnSimObject(DataReq.Flight, DataDef.Flight,
                SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        }
        catch (Exception ex) { Logger.Error($"SimConnect setup error: {ex.Message}"); }
    }

    private void OnSimQuit(SimConnect sender, SIMCONNECT_RECV data) =>
        HandleSimDisconnect("MSFS closed");

    private void OnSimException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        // Exception 7 = NAME_UNRECOGNIZED (a SimVar name MSFS doesn't know — non-fatal, other vars still work)
        string kind = data.dwException switch {
            1  => "ERROR",
            2  => "SIZE_MISMATCH",
            3  => "UNRECOGNIZED_ID",
            5  => "UNOPENED",
            6  => "VERSION_MISMATCH",
            7  => "NAME_UNRECOGNIZED",
            8  => "ALREADY_SUBSCRIBED",
            9  => "INVALID_ENUM",
            10 => "DEFINITION_ERROR",
            11 => "DUPLICATE_ID",
            12 => "DATUM_ID",
            13 => "OUT_OF_BOUNDS",
            _  => $"#{data.dwException}"
        };
        Logger.Warn($"SimConnect exception: {kind} (sendID={data.dwSendID}) — non-fatal, continuing");
    }

    private void OnSimObjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        _lastSimDataUtc = DateTime.UtcNow;   // proof the message pump is alive — see _watchdogTimer
        if ((DataReq)data.dwRequestID == DataReq.Flight)
            ProcessFlight((FlightData)data.dwData[0]);
    }

    private void OnSimObjectDataByType(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
    {
        if ((DataReq)data.dwRequestID == DataReq.Title)
            _aircraftTitle = ((TitleData)data.dwData[0]).Title;
    }

    // True only once `raw` has been continuously true for `minHold`; resets the moment
    // `raw` goes false. Used to filter MSFS's SU5+ micropauses out of both the pause bit
    // and the Active Pause bit before either is acted on or displayed.
    private static bool HeldFor(bool raw, ref DateTime? sinceUtc, TimeSpan minHold)
    {
        if (!raw) { sinceUtc = null; return false; }
        sinceUtc ??= DateTime.UtcNow;
        return DateTime.UtcNow - sinceUtc.Value >= minHold;
    }

    private void ProcessFlight(FlightData fd)
    {
        // PAUSE STATE is a bitmask, not a plain enum: bit 0 (1) = full pause,
        // bit 1 (2) = legacy/unused, bit 2 (4) = Active Pause, bit 3 (8) = sim-only pause.
        // Check the full-pause bit rather than requiring an exact match — MSFS can report
        // combined values, and evidently other bits beyond 0-3 can be set too (a raw value
        // outside the small 0-15 range some testing assumed was "the whole valid range"
        // turned out to just be one of those — not corruption). Reading via bitwise AND on
        // specific bits is correct regardless of what else is set elsewhere in the int, so
        // there's no need to validate the full value at all.
        bool nowPausedRaw = (fd.Paused & 1) != 0;
        _lastFlight        = fd;

        // Confirm we're in an actual flight (not the main menu).
        // Main menu: PAUSE STATE=1 continuously with altitude=0, speed=0.
        // Real flight: either the sim runs (paused=0) at some point, or we have
        // non-zero altitude/speed indicating a loaded scenario.
        if (!nowPausedRaw || fd.Altitude > 100.0 || fd.GroundSpeed > 5.0)
            _flightActive = true;

        // Gate entry into the paused state machine behind PauseConfirmDelay so SU5+ engine
        // hitches don't trigger a network check / log spam every couple of seconds while
        // flying normally. Once genuinely paused, track the raw bit directly so resume and
        // the network-restore flow stay fully responsive — the filter only applies on entry.
        bool nowPaused = _isPaused
            ? nowPausedRaw
            : HeldFor(nowPausedRaw, ref _rawPauseSinceUtc, PauseConfirmDelay);

        // Same filter for the "Active pause" status display (bit 2) — it drives no logic,
        // only the label, but was flickering just as much as the full-pause bit.
        _activePauseConfirmed = HeldFor((fd.Paused & 4) != 0, ref _rawActivePauseSinceUtc, PauseConfirmDelay);

        if (nowPaused && !_isPaused)
        {
            _isPaused            = true;
            _pauseStartUtc       = DateTime.UtcNow;
            _unpauseSent         = false;
            _pauseNetworkArmed   = false;
            _pauseNetworkChecked = false;
            _lastRestoreCheckUtc = null;
            _dnsRestoredUtc      = null;
            _dnsStabilityCheckUtc = null;
            _requiredDnsHold     = ShortOutageHold;
            _lastUnpauseSentUtc  = null;
            _unpauseAttempts     = 0;

            if (_flightActive)
            {
                Logger.Info("Simulator paused — checking network to determine cause…");
                // Fire an immediate async network check.
                // Manual ESC pause → internet fine → do nothing.
                // Network-caused pause → DNS/internet down → arm auto-unpause.
                var thisPauseStart = _pauseStartUtc;
                _ = Task.Run(async () =>
                {
                    var status = await _network.CheckNowAsync();

                    // If the check was INCONCLUSIVE (this PC out of local ports — chronic here,
                    // says nothing about the internet), re-check every InconclusiveRetryInterval
                    // for up to InconclusiveDecisionWindow waiting for a real answer. Still
                    // inconclusive after that → treat as intentional and leave it alone: when
                    // in doubt, NEVER risk auto-unpausing a Pause-at-TOD.
                    var decideBy = DateTime.UtcNow + InconclusiveDecisionWindow;
                    while (status.Inconclusive && DateTime.UtcNow < decideBy)
                    {
                        await Task.Delay(InconclusiveRetryInterval);
                        if (_pauseStartUtc != thisPauseStart) return;   // pause ended meanwhile
                        status = await _network.CheckNowAsync();
                    }
                    if (_pauseStartUtc != thisPauseStart) return;

                    if (status.Inconclusive)
                    {
                        _pauseNetworkArmed   = false;
                        _pauseNetworkChecked = true;
                        Logger.Warn($"Pause cause: UNKNOWN — network checks stayed inconclusive for " +
                                    $"{InconclusiveDecisionWindow.TotalSeconds:0}s (PC out of local ports). " +
                                    "Erring on the safe side: assuming intentional pause (ESC/TOD), auto-unpause DISABLED");
                        return;
                    }

                    // Arm when self.events.data.microsoft.com can't be resolved OR can't be
                    // reached over TCP — DNS alone can succeed while an actual connection
                    // still fails (e.g. a cached DNS answer being served with no packets sent
                    // at all), which would read as "internet fine" and wrongly leave a real
                    // outage pause untouched. (Port exhaustion is excluded above.)
                    bool netDown = !status.InternetOk;

                    // ...and arm as well if the network was broken RECENTLY, even though it
                    // looks fine this instant — see RecentOutageGrace for why MSFS's reaction
                    // lag makes the live-only reading miss real outage pauses. Measured back
                    // from when the PAUSE STARTED (not now), so time spent above waiting out an
                    // inconclusive spell can't widen the window. Only real failures count —
                    // inconclusive results never stamp LastFailureUtc.
                    DateTime? lastFail   = _network.LastFailureUtc;
                    DateTime  pauseStart = thisPauseStart ?? DateTime.UtcNow;
                    double    sinceFail  = lastFail.HasValue
                                             ? (pauseStart - lastFail.Value).TotalSeconds
                                             : double.MaxValue;
                    // Backward-looking only: a failure stamped more than a few seconds AFTER the
                    // pause started (e.g. during the inconclusive retries) does not count — the
                    // "never arm on a drop after the pause" rule protects TOD. The 10s slack only
                    // covers a check that was already in flight when the pause began. Also rejects
                    // a failure stamped "in the future" (backward clock adjustment, NTP sync).
                    bool failNotFuture = !lastFail.HasValue || lastFail.Value <= DateTime.UtcNow;
                    bool recentOutage = !netDown
                                        && failNotFuture
                                        && sinceFail >= -10
                                        && sinceFail <= RecentOutageGrace.TotalSeconds;

                    _pauseNetworkArmed   = netDown || recentOutage;
                    _pauseNetworkChecked = true;

                    if (netDown)
                        Logger.Info("Pause cause: self.events.data.microsoft.com unreachable — " +
                                    "auto-unpause ARMED (live check failed)");
                    else if (recentOutage)
                        Logger.Warn($"Pause cause: network ok right now, but it failed {sinceFail:0}s ago — " +
                                    $"inside the {RecentOutageGrace.TotalMinutes:0}min grace window, so this is being " +
                                    "treated as outage-caused, NOT intentional — auto-unpause ARMED (recent outage)");
                    else
                        Logger.Info("Pause cause: DNS+TCP ok — assuming intentional pause (ESC/TOD), auto-unpause DISABLED");
                });
            }
            else
            {
                Logger.Info("Simulator paused — ignoring (no active flight loaded yet)");
                _pauseNetworkChecked = true; // skip network check, not relevant
            }
        }
        else if (!nowPaused && _isPaused)
        {
            double wasFor = _pauseStartUtc.HasValue ? (DateTime.UtcNow - _pauseStartUtc.Value).TotalSeconds : 0;
            _isPaused = false; _pauseStartUtc = null; _unpauseSent = false;
            _pauseNetworkArmed = false; _pauseNetworkChecked = false;
            _lastRestoreCheckUtc = null; _dnsRestoredUtc = null;
            _dnsStabilityCheckUtc = null; _requiredDnsHold = ShortOutageHold;
            _lastUnpauseSentUtc = null; _unpauseAttempts = 0;
            Logger.Info($"Simulator resumed (was paused {wasFor:0}s)");
        }
        else if (_isPaused && !_unpauseSent && _cfg.Enabled && _flightActive)
        {
            // Only auto-unpause if the initial network check confirmed both DNS and ping were down.
            // We never re-arm: if internet was fine at pause time the pause is treated as
            // intentional (ESC, Pause-at-TOD, etc.) regardless of later network changes.
            if (!_pauseNetworkChecked || !_pauseNetworkArmed) return;

            double elapsed = _pauseStartUtc.HasValue ? (DateTime.UtcNow - _pauseStartUtc.Value).TotalSeconds : 0;
            if (elapsed < _cfg.PauseThresholdSeconds) return;

            // Past the minimum hold time — now wait for internet to come back.
            // MSFS shows "Connection to network lost" while offline; only once it
            // transitions to the pause menu (Resume/Restart/etc) can Escape/click close it.
            // That transition happens when DNS+TCP are both healthy again, so we wait for that
            // (DNS alone can resolve while TCP is still unreachable — see InternetOk).
            var net = _network.LastStatus;
            if (net.InternetOk)
            {
                // DNS is back — MSFS is transitioning from the error screen to the pause menu.
                // Hold before sending resume so MSFS can finish that transition. How long we
                // hold scales with how long the outage was: a brief blip keeps the old snappy
                // 5s hold, but a long outage — the kind that leaves MSFS still reconnecting
                // internally — gets a longer hold, picked once at the moment DNS is first
                // observed back.
                if (!_dnsRestoredUtc.HasValue)
                {
                    _dnsRestoredUtc       = DateTime.UtcNow;
                    _dnsStabilityCheckUtc = DateTime.UtcNow;
                    double outageSecs  = _pauseStartUtc.HasValue ? (DateTime.UtcNow - _pauseStartUtc.Value).TotalSeconds : 0;
                    _requiredDnsHold   = outageSecs >= LongOutageThreshold.TotalSeconds ? LongOutageHold : ShortOutageHold;
                    Logger.Info($"DNS restored after a {outageSecs:0}s outage — waiting {_requiredDnsHold.TotalSeconds:0}s " +
                                "of confirmed-stable connectivity before resuming…");
                }

                // Actively re-verify DNS through the hold rather than trusting one stale
                // reading — LastStatus otherwise only refreshes via the 60s background
                // watchdog, which could easily miss a flap inside a 5-20s window. A flap
                // detected here drops us into the "DNS still down" branch below, which
                // resets the hold entirely rather than letting the timer expire regardless.
                if (!_dnsStabilityCheckUtc.HasValue ||
                    (DateTime.UtcNow - _dnsStabilityCheckUtc.Value) >= DnsRecheckInterval)
                {
                    _dnsStabilityCheckUtc = DateTime.UtcNow;
                    _ = Task.Run(async () => await _network.CheckNowAsync());
                }

                if ((DateTime.UtcNow - _dnsRestoredUtc.Value) >= _requiredDnsHold)
                    SendUnpause();
                return;
            }

            // DNS still down, or flapped back down mid-hold — reset and go back to waiting.
            // A flap after we'd already started a hold is exactly the unstable-reconnect
            // window we're trying to avoid firing Escape into, so it's worth its own log line.
            // (An inconclusive port-exhaustion reading also lands here — we can't confirm the
            // network is up, so we don't resume on it; same conservative reset.)
            if (_dnsRestoredUtc.HasValue)
                Logger.Warn(net.Inconclusive
                    ? "Network check went inconclusive (PC out of local ports) during the stability hold — restarting wait"
                    : "DNS dropped again during the stability hold — restarting wait for reconnection");
            _dnsRestoredUtc       = null;
            _dnsStabilityCheckUtc = null;

            // Poll for restoration every 15s (background watchdog is every 60s, too slow).
            if (!_lastRestoreCheckUtc.HasValue ||
                (DateTime.UtcNow - _lastRestoreCheckUtc.Value).TotalSeconds >= 15)
            {
                _lastRestoreCheckUtc = DateTime.UtcNow;
                _ = Task.Run(async () => await _network.CheckNowAsync());
            }
        }
        else if (_isPaused && _unpauseSent && _cfg.Enabled && _pauseNetworkArmed)
        {
            // Unpause attempt was made but sim is still paused — MSFS may not have been
            // ready yet (still transitioning from error screen). Retry every 10s.
            if (_lastUnpauseSentUtc.HasValue &&
                (DateTime.UtcNow - _lastUnpauseSentUtc.Value).TotalSeconds >= 10)
            {
                Logger.Info($"Sim still paused after unpause attempt {_unpauseAttempts} — retrying…");
                _unpauseSent = false;   // allow the armed block to fire again next tick
            }
        }
    }

    private void SendUnpause()
    {
        if (_sim == null || !_simConnected)
        {
            Logger.Warn("SendUnpause: aborted — SimConnect not connected");
            return;
        }
        _unpauseSent       = true;
        _lastUnpauseSentUtc = DateTime.UtcNow;
        _unpauseAttempts++;
        _lastUnpauseUtc    = DateTime.UtcNow;

        double elapsed  = _pauseStartUtc.HasValue ? (DateTime.UtcNow - _pauseStartUtc.Value).TotalSeconds : 0;
        var    netNow   = _network.LastStatus;

        Logger.Warn($"Auto-unpause triggered (paused {elapsed:0}s)");
        Logger.Diag("=== Auto-Unpause Diagnostic ===");
        Logger.Diag($"  UTC Time     : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        Logger.Diag($"  Aircraft     : {_aircraftTitle}");
        Logger.Diag($"  Altitude     : {_lastFlight.Altitude:F0} ft");
        Logger.Diag($"  Ground Speed : {_lastFlight.GroundSpeed:F0} kts");
        Logger.Diag($"  Paused For   : {elapsed:F0}s");
        Logger.Diag($"  DNS          : {(netNow.DnsOk     ? "OK" : $"FAILED ({netNow.DnsError})")}");
        Logger.Diag($"  Internet     : {(netNow.InternetOk ? "OK" : $"FAILED ({netNow.InternetError})")}");
        Logger.Diag($"  DNS Hold Used: {_requiredDnsHold.TotalSeconds:0}s ({(_requiredDnsHold == LongOutageHold ? "long-outage" : "short-outage")} tier)");
        Logger.Diag($"  PAUSE STATE  : raw={_lastFlight.Paused} (bit0 full-pause={(_lastFlight.Paused & 1) != 0})");
        Logger.Diag($"  Attempt #    : {_unpauseAttempts}");
        Logger.Diag("================================");

        try { _sim.RequestDataOnSimObjectType(DataReq.Title, DataDef.Title, 0, SIMCONNECT_SIMOBJECT_TYPE.USER); } catch { }

        // Persist the event so the UI card survives app restarts
        _cfg.LastUnpauseUtc        = DateTime.UtcNow;
        _cfg.LastUnpauseAircraft   = _aircraftTitle;
        _cfg.LastUnpauseAltitudeFt = _lastFlight.Altitude;
        _cfg.LastUnpausePausedSecs = elapsed;
        _cfg.Save();

        string dur = elapsed >= 60
            ? $"{Math.Floor(elapsed / 60):0}m {elapsed % 60:0}s"
            : $"{elapsed:0}s";
        _tray.ShowBalloonTip(6000, "✈ MSFS Auto Unpause",
            $"Sim unpaused after {dur} pause.\n{_aircraftTitle} · {_lastFlight.Altitude:N0} ft",
            ToolTipIcon.Info);

        // No PAUSE_SET — confirmed across 1500+ real attempts it never closes MSFS's own
        // pause menu. Every attempt forces focus onto the MSFS window first (from attempt
        // 1, not deferred). Attempts 1-2 use Escape, the cheaper/less invasive mechanism;
        // attempt 3+ escalates to the OCR-located mouse click as its own distinct tier
        // rather than stacking on top of Escape, so the log cleanly shows which single
        // mechanism was in play when the sim actually resumed.
        if (_unpauseAttempts <= 2) TryForceCloseMenu();
        else                       TryClickResume();
    }

    private void HandleSimDisconnect(string reason)
    {
        if (!_simConnected && _sim == null) return;
        Logger.Info($"SimConnect disconnected: {reason}");
        _simConnected = false; _isPaused = false; _pauseStartUtc = null;
        _unpauseSent = false; _aircraftTitle = "Unknown"; _flightActive = false;
        _pauseNetworkArmed = false; _pauseNetworkChecked = false;
        _lastRestoreCheckUtc = null; _dnsRestoredUtc = null;
        _dnsStabilityCheckUtc = null; _requiredDnsHold = ShortOutageHold;
        _lastUnpauseSentUtc = null; _unpauseAttempts = 0;
        try { _sim?.Dispose(); } catch { }
        _sim = null;
    }

    private static Icon MakeTrayIcon(Color color)
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0)); g.FillEllipse(sh, 3, 4, 26, 26);
            using var fi = new SolidBrush(color);                        g.FillEllipse(fi, 2, 2, 26, 26);
            using var ri = new Pen(Color.FromArgb(80, 255, 255, 255), 1.5f); g.DrawEllipse(ri, 3, 3, 23, 23);
            using var f  = new Font("Segoe UI", 11f, FontStyle.Bold);
            using var t  = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            var sz = g.MeasureString("M", f);
            g.DrawString("M", f, t, (32 - sz.Width) / 2f, (32 - sz.Height) / 2f - 1f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watchdogTimer?.Dispose();
        _network.Dispose();
        HandleSimDisconnect("app closing");
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }
}
