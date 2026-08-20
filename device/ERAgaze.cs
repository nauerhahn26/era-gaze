// RaeGaze v2 — standalone gaze service for Tobii devices (TD I-13 and any other
// Tobii Stream Engine tracker). Runs locally, inherits the device's own calibration
// (gaze_point is post-calibration), and does exactly two jobs:
//
//   1. CURSOR: moves the pointer to a stabilized gaze point and draws a soft ring
//      showing where she's looking. It does NOT click (dwell-click is opt-in for
//      caregiver/QA use). Activation belongs to apps (web/dwell.js, AsTeRICS hover).
//   2. GAZE BUS: serves gaze samples on a local WebSocket + GET /status, using the
//      Aivota sidecar contract, so apps can consume gaze directly (no OS cursor):
//        ws://127.0.0.1:49155  ->  {"gazePoint":{"x":0-1,"y":0-1},"validity":0|1,"timestamp":us}
//      (plus extra fields: px, py, locked, suppressed — consumers may ignore them)
//
// v2 fixes (from the Fable 5 review):
//   - filtering runs in the Tobii callback thread, in PIXEL space, with device
//     timestamps (v1 filtered normalized coords on a UI timer: beta was inert -> lag)
//   - track loss (blink/look-away/head turn) PAUSES everything: brief loss holds the
//     lock (forgiveness); sustained loss unlocks and PARKS the cursor at a rest spot
//     so a frozen cursor can never complete someone else's hover-dwell
//   - fixation lock releases only on a SUSTAINED exit (BreakSamples consecutive
//     samples beyond BreakRadius), so single-sample flicks/blink artifacts can't unlock
//   - JSON config with live reload (tune during a session, no rebuild), metrics logging
//   - tracker connect is a retry loop: works as an always-on service on any Tobii device
//
// v2.3: integrated volume cap (merged from board-builder tools/volume-cap volcap.cs,
//   dad's 8/19 ruling) — CoreAudio clamp of the master volume at a configurable cap.
//   SHIPS OFF ("VolCapEnabled": false): families opt in per device in ERAgaze.json.
//
// v2.4: public-release configurability — the three family/vendor couplings are now
//   configurable with compat defaults (existing devices set nothing and change NOTHING):
//   BaseDir (--base=<path> arg / ERAGAZE_BASE env / default C:\Users\Public\RaeGaze),
//   "ForegroundApp" (the shell: URI /app/exit hands the screen back to; default TD Snap),
//   "DenyApps" (foreground processes where the cursor steps aside; default Tobii/Snap list).
//
// Sources:  --source=gaze (default) | --source=mouse (simulation without eyes)
// Config:   <BaseDir>\ERAgaze.json (created with defaults on first run;
//           edits apply live within ~2s). CLI flags override the file for that run.
//
// Build (on device):
//   csc /target:winexe /platform:x64 /out:RaeGaze.exe ^
//       /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Management.dll RaeGaze.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

static class Cfg {
    // ---- source / behavior ----
    public static string Source = "gaze";
    public static bool DwellClick = false;   // off = cursor + ring only; apps own activation
    public static int DwellMs = 800;         // (opt-in dwell-click only)
    public static int YieldMs = 1500;        // after real touch/mouse input, leave the pointer alone this long
    public static bool Force = false;        // testing: ignore TD-Snap suppression
    public static bool Paused = false;       // parent test toggle: fully inert (no cursor move, no ring,
                                             //   no bus samples) but HTTP /status + /config stay alive to un-pause

    // ---- stabilization (PIXEL space; One Euro per Casiez CHI 2012) ----
    public static int Median = 3;            // median spike-reject window
    public static double MinCutoff = 1.0;    // Hz floor: lower = calmer when still
    public static double Beta = 0.007;       // speed coefficient (pixel-space scale — adaptivity live again)
    public static double DCutoff = 1.0;

    // ---- fixation lock ----
    public static int LockRadius = 40;       // hold within this to lock (~1 deg on the I-13 panel)
    public static int LockOnMs = 250;        // hold time before the cursor freezes on a fixation
    public static int BreakRadius = 100;     // exit distance to unlock ... (review: was 70)
    public static int BreakSamples = 3;      // ... sustained for this many consecutive samples

    // ---- track loss (the false-activation fix) ----
    public static int FreshMs = 150;         // a sample older than this counts as loss
    public static int GraceMs = 300;         // loss shorter than this: hold lock + cursor (blink forgiveness)
    public static int ParkAfterMs = 600;     // loss longer than this: park the cursor at the rest spot
    public static bool ParkOnLoss = true;
    public static double ParkX01 = 0.995, ParkY01 = 0.995; // rest spot (boards keep this corner empty)
    // Transient per-app park override (POST /app/park) — in-memory ONLY, never written to
    // ERAgaze.json: a persisted center-park would be dangerous under hover-driven apps
    // (AsTeRICS). Cleared by /app/exit, so leaving an app always restores the corner.
    public static double? ParkOvX = null, ParkOvY = null;
    public static int FilterResetMs = 500;   // sample gap after which filters restart (no velocity spike)

    // ---- gaze bus ----
    public static bool Bus = true;
    public static int Port = 49155;          // same contract as Aivota, our own port (theirs owns 49152)

    // ---- integrated volume cap (merged from tools/volume-cap volcap.cs, 8/19) ----
    // DEFAULT IS OFF AND MUST STAY OFF: public releases must never cap a family's
    // volume unasked. Each family opts in PER DEVICE by setting "VolCapEnabled": true
    // (and tuning "VolCapLevel") in ERAgaze.json — hot-reload applies it within ~2s.
    public static bool VolCapEnabled = false;
    public static int VolCapLevel = 55;      // clamp target, 0-100 (% of master volume)

    // ---- dwell-click extras (opt-in only) ----
    public static int Radius = 26;           // ring visual radius
    public static int Tol = 55;              // dwell target wander tolerance
    public static int ReArm = 72;            // must leave this far after a click before re-dwelling
    public static int CooldownMs = 300;

    // ---- base directory (v2.4, public release: ONE resolution point for every on-disk
    // path — config, log, owner/pid, boot marker, and the Chrome-kiosk profile matcher).
    // First of: --base=<path> command-line arg, ERAGAZE_BASE environment variable, else
    // the historical C:\Users\Public\RaeGaze. That default is the compat guarantee:
    // Ellie's devices set neither override, so every path is byte-identical to v2.3.
    public static readonly string BaseDir = ResolveBaseDir();
    static string ResolveBaseDir() {
        try {
            var args = Environment.GetCommandLineArgs();   // resolved before Main's own arg loop runs
            for (int i = 1; i < args.Length; i++)
                if (args[i].StartsWith("--base=") && args[i].Length > 7)
                    return args[i].Substring(7).TrimEnd('\\', '/');
            string env = Environment.GetEnvironmentVariable("ERAGAZE_BASE");
            if (!string.IsNullOrEmpty(env)) return env.TrimEnd('\\', '/');
        } catch { }
        return @"C:\Users\Public\RaeGaze";
    }
    public static string LogPath = Path.Combine(BaseDir, "eragaze.log");
    public static string ConfigPath = Path.Combine(BaseDir, "ERAgaze.json");
    // App that /app/exit hands the screen back to after closing our kiosks (v2.4, public
    // release: not every family runs TD Snap). shell: URIs ONLY, launched via explorer.exe
    // (e.g. shell:AppsFolder\<AUMID>!App); "" = foreground nothing, just close the kiosks.
    // Config key "ForegroundApp" in ERAgaze.json (hot-reload). Default = TD Snap (compat).
    public static string ForegroundApp = @"shell:AppsFolder\TobiiDynavox.Snap_626b2w651dr5w!App";
    // foreground processes where the cursor must step aside — their own gaze runs there.
    // TD Snap + Tobii UIs + Aivota. v2.4, public release: config key "DenyApps" in
    // ERAgaze.json (array of strings, lowercase substring match against the foreground
    // process name — exactly these semantics; "deny" is the accepted legacy spelling).
    // Default = the current list (compat).
    public static string[] Deny = { "snap", "tobii", "eyeassist", "tdx.computercontrol", "aivota" };

    // Load JSON config (if present) over the defaults above. Returns true if parsed.
    public static bool LoadFile() {
        try {
            if (!File.Exists(ConfigPath)) return false;
            var ser = new JavaScriptSerializer();
            var d = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath));
            if (d == null) return false;
            DwellClick = B(d, "dwellClick", DwellClick);
            Paused = B(d, "paused", Paused);
            DwellMs = I(d, "dwellMs", DwellMs); YieldMs = I(d, "yieldMs", YieldMs);
            Median = Math.Max(1, I(d, "median", Median));
            MinCutoff = D(d, "minCutoff", MinCutoff); Beta = D(d, "beta", Beta); DCutoff = D(d, "dCutoff", DCutoff);
            LockRadius = I(d, "lockRadius", LockRadius); LockOnMs = I(d, "lockOnMs", LockOnMs);
            BreakRadius = I(d, "breakRadius", BreakRadius); BreakSamples = Math.Max(1, I(d, "breakSamples", BreakSamples));
            FreshMs = I(d, "freshMs", FreshMs); GraceMs = I(d, "graceMs", GraceMs);
            ParkAfterMs = I(d, "parkAfterMs", ParkAfterMs); ParkOnLoss = B(d, "parkOnLoss", ParkOnLoss);
            ParkX01 = D(d, "parkX01", ParkX01); ParkY01 = D(d, "parkY01", ParkY01);
            FilterResetMs = I(d, "filterResetMs", FilterResetMs);
            Bus = B(d, "bus", Bus); Port = I(d, "port", Port);
            VolCapEnabled = B(d, "VolCapEnabled", VolCapEnabled);
            VolCapLevel = Math.Max(0, Math.Min(100, I(d, "VolCapLevel", VolCapLevel)));
            ForegroundApp = S(d, "ForegroundApp", ForegroundApp);   // "" is a valid value (foreground nothing)
            object dv;   // "DenyApps" (v2.4 public name) wins; "deny" kept so existing device configs keep working
            if ((d.TryGetValue("DenyApps", out dv) || d.TryGetValue("deny", out dv)) && dv is System.Collections.ArrayList) {
                var list = new List<string>();
                foreach (object o in (System.Collections.ArrayList)dv) list.Add(Convert.ToString(o).ToLowerInvariant());
                if (list.Count > 0) Deny = list.ToArray();
            }
            Radius = I(d, "radius", Radius); Tol = I(d, "tol", Tol); ReArm = I(d, "reArm", ReArm);
            CooldownMs = I(d, "cooldownMs", CooldownMs);
            return true;
        } catch (Exception ex) { Log.W("config load failed: " + ex.Message); return false; }
    }

    // First run: write a template the caregiver team can edit (applies live within ~2s).
    public static void WriteDefault() {
        try {
            // migrate the old RaeGaze.json name if present (rebrand to ERAgaze)
            string old = Path.Combine(BaseDir, "RaeGaze.json");
            if (!File.Exists(ConfigPath) && File.Exists(old)) { File.Copy(old, ConfigPath); return; }
            if (File.Exists(ConfigPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath,
"{\n" +
"  \"_doc\": \"RaeGaze tuning. Edits apply live (~2s). Times in ms, distances in px.\",\n" +
"  \"dwellClick\": false,\n" +
"  \"paused\": false,\n" +
"  \"dwellMs\": 800,\n" +
"  \"yieldMs\": 1500,\n" +
"  \"median\": 3,\n" +
"  \"minCutoff\": 1.0,\n" +
"  \"beta\": 0.007,\n" +
"  \"lockRadius\": 40,\n" +
"  \"lockOnMs\": 250,\n" +
"  \"breakRadius\": 100,\n" +
"  \"breakSamples\": 3,\n" +
"  \"_doc2\": \"Track loss: <graceMs holds through a blink; >parkAfterMs parks the cursor at the rest corner.\",\n" +
"  \"freshMs\": 150,\n" +
"  \"graceMs\": 300,\n" +
"  \"parkAfterMs\": 600,\n" +
"  \"parkOnLoss\": true,\n" +
"  \"parkX01\": 0.995,\n" +
"  \"parkY01\": 0.995,\n" +
"  \"filterResetMs\": 500,\n" +
"  \"_doc3\": \"DenyApps: foreground process-name substrings (lowercase) where the cursor steps aside. ForegroundApp: shell: URI /app/exit foregrounds after closing kiosks ('' = none).\",\n" +
"  \"DenyApps\": [\"snap\", \"tobii\", \"eyeassist\", \"tdx.computercontrol\", \"aivota\"],\n" +
"  \"ForegroundApp\": \"shell:AppsFolder\\\\TobiiDynavox.Snap_626b2w651dr5w!App\",\n" +
"  \"bus\": true,\n" +
"  \"port\": 49155,\n" +
"  \"_doc4\": \"Volume cap: ships OFF — opt in per device. true = master volume above VolCapLevel is clamped back down (mute + volume-down never touched).\",\n" +
"  \"VolCapEnabled\": false,\n" +
"  \"VolCapLevel\": 55,\n" +
"}\n");
        } catch (Exception ex) { Log.W("config write failed: " + ex.Message); }
    }

    static int I(Dictionary<string, object> d, string k, int def) { object v; return d.TryGetValue(k, out v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : def; }
    static string S(Dictionary<string, object> d, string k, string def) { object v; return d.TryGetValue(k, out v) ? (Convert.ToString(v) ?? "") : def; }
    static double D(Dictionary<string, object> d, string k, double def) { object v; return d.TryGetValue(k, out v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def; }
    static bool B(Dictionary<string, object> d, string k, bool def) { object v; return d.TryGetValue(k, out v) ? Convert.ToBoolean(v, CultureInfo.InvariantCulture) : def; }
}

// ---- Tobii Stream Engine interop (any local Tobii tracker; calibration is the device's own) ----
static class Tobii {
    [StructLayout(LayoutKind.Sequential)] public struct Ver { public int major, minor, revision, build; }
    [StructLayout(LayoutKind.Sequential)] public struct GazePoint { public long ts; public int validity; public float x, y; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void UrlRecv([MarshalAs(UnmanagedType.LPStr)] string url, IntPtr u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GazeCb(ref GazePoint gp, IntPtr u);
    [DllImport("tobii_stream_engine.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int tobii_get_api_version(ref Ver v);
    [DllImport("tobii_stream_engine.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int tobii_api_create(out IntPtr api, IntPtr a, IntPtr l);
    [DllImport("tobii_stream_engine.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int tobii_api_destroy(IntPtr api);
    [DllImport("tobii_stream_engine.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int tobii_enumerate_local_device_urls(IntPtr api, UrlRecv r, IntPtr u);
    [DllImport("tobii_stream_engine.dll", EntryPoint = "tobii_device_create", CallingConvention = CallingConvention.Cdecl)] public static extern int device_create4(IntPtr api, string url, int fou, out IntPtr dev);
    [DllImport("tobii_stream_engine.dll", EntryPoint = "tobii_device_create", CallingConvention = CallingConvention.Cdecl)] public static extern int device_create3(IntPtr api, string url, out IntPtr dev);
    [DllImport("tobii_stream_engine.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int tobii_device_destroy(IntPtr dev);
    [DllImport("tobii_stream_engine.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int tobii_gaze_point_subscribe(IntPtr dev, GazeCb cb, IntPtr u);
    [DllImport("tobii_stream_engine.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int tobii_gaze_point_unsubscribe(IntPtr dev);
    [DllImport("tobii_stream_engine.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int tobii_device_process_callbacks(IntPtr dev);
}

static class Log {
    static readonly object l = new object();
    public static void W(string s) {
        try {
            lock (l) {
                // simple size cap so an always-on service never fills the disk
                var fi = new FileInfo(Cfg.LogPath);
                if (fi.Exists && fi.Length > 4 * 1024 * 1024) {
                    string old = Cfg.LogPath + ".1"; if (File.Exists(old)) File.Delete(old); File.Move(Cfg.LogPath, old);
                }
                File.AppendAllText(Cfg.LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
            }
        } catch { }
    }
}

// --- signal filters: median spike-reject, then One Euro adaptive low-pass ---
sealed class MedianN {
    readonly double[] buf; int n, count, idx;
    public MedianN(int size) { n = Math.Max(1, size); buf = new double[n]; }
    public double Filter(double x) {
        buf[idx] = x; idx = (idx + 1) % n; if (count < n) count++;
        var t = new double[count]; Array.Copy(buf, t, count); Array.Sort(t);
        return t[count / 2];
    }
}
sealed class LowPass {
    double s; bool init;
    public bool Initialized { get { return init; } }
    public double Last { get { return s; } }
    public double Filter(double x, double alpha) {
        if (!init) { s = x; init = true; } else { s = alpha * x + (1 - alpha) * s; }
        return s;
    }
}
// One Euro (Casiez et al., CHI 2012): heavy smoothing when still, light during saccades.
// v2: fed PIXEL coordinates with DEVICE timestamps, so beta's speed term is meaningful.
sealed class OneEuro {
    double freq, mincutoff, beta, dcutoff, lastT = -1;
    readonly LowPass xf = new LowPass(), dxf = new LowPass();
    public OneEuro(double freq, double mincutoff, double beta, double dcutoff) {
        this.freq = freq; this.mincutoff = mincutoff; this.beta = beta; this.dcutoff = dcutoff;
    }
    double Alpha(double cutoff) { double tau = 1.0 / (2 * Math.PI * cutoff); double te = 1.0 / freq; return 1.0 / (1.0 + tau / te); }
    public double Filter(double x, double t) {
        if (lastT >= 0 && t > lastT) freq = 1.0 / (t - lastT);
        lastT = t;
        double dx = xf.Initialized ? (x - xf.Last) * freq : 0.0;
        double edx = dxf.Filter(dx, Alpha(dcutoff));
        double cutoff = mincutoff + beta * Math.Abs(edx);
        return xf.Filter(x, Alpha(cutoff));
    }
}

// Full stabilization pipeline. Owned by exactly ONE thread:
// the Tobii callback thread (gaze mode) or the UI timer (mouse simulation mode).
sealed class Pipeline {
    MedianN mx, my; OneEuro ex, ey; double lastT = -1;
    public Pipeline() { Reset(); }
    public void Reset() {
        mx = new MedianN(Cfg.Median); my = new MedianN(Cfg.Median);
        ex = new OneEuro(60.0, Cfg.MinCutoff, Cfg.Beta, Cfg.DCutoff);
        ey = new OneEuro(60.0, Cfg.MinCutoff, Cfg.Beta, Cfg.DCutoff);
        lastT = -1;
    }
    // px/py in pixels, t in seconds (device time). Auto-resets after a long gap.
    public void Filter(double pxIn, double pyIn, double t, out double px, out double py) {
        if (lastT >= 0 && (t - lastT) * 1000.0 > Cfg.FilterResetMs) Reset();
        lastT = t;
        px = ex.Filter(mx.Filter(pxIn), t);
        py = ey.Filter(my.Filter(pyIn), t);
    }
}

// Latest FILTERED sample, produced by the callback thread, consumed by the UI timer.
sealed class Filtered {
    public readonly object Lock = new object();
    public long Seq;            // bumps on every fresh sample
    public bool Valid;          // tracker validity of the latest sample
    public double Px, Py;       // filtered pixels
    public double X01, Y01;     // filtered normalized (for the bus)
    public int StampTick;       // Environment.TickCount at receipt
}

// Per-pixel-alpha, click-through, topmost, non-activating overlay.
sealed class Overlay : Form {
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8,
              WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr scr, ref POINT dst, ref SIZE sz, IntPtr src, ref POINT psrc, int key, ref BLENDFUNCTION bf, int flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    public Overlay() {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        TopMost = true; Bounds = new Rectangle(-100, -100, 8, 8);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { var c = base.CreateParams; c.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return c; }
    }
    public void SetFrame(Bitmap bmp, int x, int y) {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0)); old = SelectObject(memDc, hBmp);
            var size = new SIZE(bmp.Width, bmp.Height); var pdst = new POINT(x, y); var psrc = new POINT(0, 0);
            var bf = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref pdst, ref size, memDc, ref psrc, 0, ref bf, 2);
        } finally {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            DeleteDC(memDc);
        }
    }
    // Re-assert top-of-Z. A full-screen Chrome kiosk (Book Reader, Making Words, the
    // Outfits picker) can leapfrog a topmost layered window via fullscreen optimizations,
    // hiding the ring while Windows still paints the pointer on top. Nudging back to
    // HWND_TOPMOST keeps the ring above the kiosk — no ERAgaze restart needed.
    public void ReassertTopmost() {
        try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
    }
}

static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MInput mi; }
    [StructLayout(LayoutKind.Sequential)] public struct MInput { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    const uint MOVE = 0x0001, ABS = 0x8000, LDOWN = 0x0002, LUP = 0x0004;
    public static readonly IntPtr SIG = new IntPtr(0x52470001); // marks RaeGaze's own injected input
    static int SW = Screen.PrimaryScreen.Bounds.Width, SH = Screen.PrimaryScreen.Bounds.Height;

    public static void Move(int x, int y) {
        int nx = (int)(x * 65535.0 / (SW - 1)), ny = (int)(y * 65535.0 / (SH - 1));
        var m = new INPUT { type = 0 }; m.mi = new MInput { dx = nx, dy = ny, flags = MOVE | ABS, extra = SIG };
        SendInput(1, new[] { m }, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Click(int x, int y) {
        int nx = (int)(x * 65535.0 / (SW - 1)), ny = (int)(y * 65535.0 / (SH - 1));
        var a = new INPUT { type = 0 }; a.mi = new MInput { dx = nx, dy = ny, flags = MOVE | ABS, extra = SIG };
        var d = new INPUT { type = 0 }; d.mi = new MInput { flags = LDOWN, extra = SIG };
        var u = new INPUT { type = 0 }; u.mi = new MInput { flags = LUP, extra = SIG };
        SendInput(3, new[] { a, d, u }, Marshal.SizeOf(typeof(INPUT)));
    }
    public static string ForegroundProc() {
        try { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch { return ""; }
    }
}

// ---- Integrated volume cap (merged from board-builder tools/volume-cap volcap.cs, 8/19) ----
// CoreAudio clamp: endpoint volume-change callback (<100ms) + a light 3s safety poll that
// also re-attaches when the default audio endpoint changes. NEVER touches mute and NEVER
// raises a volume — only volumes ABOVE the cap are pulled back down (dad's 8/14 ruling:
// muting / going quieter is always allowed).
// DEFAULTS OFF (Cfg.VolCapEnabled = false): released builds must not cap anyone's volume
// unasked — families opt in per device in ERAgaze.json. Toggling/changing the cap in the
// json applies via the existing config hot-reload (~2s), no restart. When disabled the
// worker unsubscribes and releases the endpoint: zero CoreAudio activity.
// Threading: all CoreAudio work runs on the worker thread + the COM callback thread
// (volcap.cs's proven standalone pattern) — never on the WinForms UI thread.

[Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr pNotifyData); }

[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
    int GetChannelCount(out uint pnChannelCount);
    int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
    int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
    int GetMasterVolumeLevel(out float pfLevelDB);
    int GetMasterVolumeLevelScalar(out float pfLevel);
    int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
    int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
    int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevelScalar);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    int GetState(out uint pdwState);
}

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IntPtr ppDevices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorComObject { }

[StructLayout(LayoutKind.Sequential)]
struct VolNotifyData { public Guid guidEventContext; public uint bMuted; public float fMasterVolume; public uint nChannels; }

// One worker per enabled span. Owns ALL of its COM state (endpoint, callback registration)
// so an old stopping worker can never stomp a fresh one. Stop -> unsubscribe + release.
sealed class VolCapWorker : IAudioEndpointVolumeCallback {
    volatile bool stop;
    IAudioEndpointVolume vol; string devId;
    public void RequestStop() { stop = true; }
    static float Cap() { return Cfg.VolCapLevel / 100f; }   // live: cap changes need no restart

    public void Run() {   // dedicated background thread (spawned by VolCapEngine.Apply)
        Log.W("volcap(integrated) ON cap=" + Cfg.VolCapLevel);
        while (!stop) {
            try {
                Attach();
                float f; vol.GetMasterVolumeLevelScalar(out f);
                VolCapEngine.LastPercent = (int)Math.Round(f * 100);
                if (f > Cap() + 0.004f) Clamp(f, "poll");
            } catch (Exception e) { Log.W("volcap(integrated) poll error: " + e.Message); vol = null; devId = null; }
            for (int i = 0; i < 12 && !stop; i++) Thread.Sleep(250);   // 3s poll, responsive stop
        }
        Detach();
        VolCapEngine.LastPercent = -1;
        Log.W("volcap(integrated) OFF — endpoint released, fully inert");
    }

    // COM callback thread: clamp directly here (never marshal to the UI thread — volcap.cs
    // proved this pattern standalone; the endpoint interface is agile).
    public int OnNotify(IntPtr pNotifyData) {
        try {
            var d = (VolNotifyData)Marshal.PtrToStructure(pNotifyData, typeof(VolNotifyData));
            VolCapEngine.LastPercent = (int)Math.Round(d.fMasterVolume * 100);
            if (!stop && d.guidEventContext != VolCapEngine.OurContext && d.fMasterVolume > Cap() + 0.004f)
                Clamp(d.fMasterVolume, "event");
        } catch { }
        return 0;
    }

    void Clamp(float from, string src) {
        try {
            IAudioEndpointVolume v = vol; if (v == null) return;
            v.SetMasterVolumeLevelScalar(Cap(), ref VolCapEngine.OurContext);   // down to cap only; mute untouched
            VolCapEngine.LastPercent = Cfg.VolCapLevel;
            Log.W(string.Format("volcap(integrated) clamp {0:F0} -> {1} ({2})", from * 100, Cfg.VolCapLevel, src));
        } catch (Exception e) { Log.W("volcap(integrated) clamp FAILED: " + e.Message); }
    }

    void Attach() {   // (re)bind the default render endpoint; re-attaches on device change
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice dev; Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out dev));
        string id; dev.GetId(out id);
        if (id == devId && vol != null) return;
        Detach();
        Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o));
        vol = (IAudioEndpointVolume)o; devId = id;
        vol.RegisterControlChangeNotify(this);
        Log.W("volcap(integrated) attached endpoint " + id + " cap=" + Cfg.VolCapLevel);
    }

    void Detach() {
        var v = vol; vol = null; devId = null;
        if (v != null) {
            try { v.UnregisterControlChangeNotify(this); } catch { }
            try { Marshal.ReleaseComObject(v); } catch { }
        }
    }
}

static class VolCapEngine {
    // our own event context (distinct from standalone volcap.exe's) so we ignore only our writes
    public static Guid OurContext = new Guid("8e22f1d6-1e6b-4b6f-8b66-d5b66f66db66");
    public static volatile int LastPercent = -1;   // newest master-volume reading; -1 = unknown/off
    static readonly object gate = new object();
    static VolCapWorker worker;

    // Reconcile running state with Cfg. Called at startup, on every config hot-reload,
    // and from POST /volcap. Idempotent; cap-level changes apply live without a restart.
    public static void Apply() {
        lock (gate) {
            if (Cfg.VolCapEnabled && worker == null) {
                var w = new VolCapWorker(); worker = w;
                var th = new Thread(w.Run); th.IsBackground = true; th.Start();
            } else if (!Cfg.VolCapEnabled && worker != null) {
                worker.RequestStop(); worker = null;
            }
        }
    }
}

// ---- Gaze bus: WebSocket broadcast + GET /status on one local port (Aivota contract) ----
sealed class Bus {
    sealed class Client { public WebSocket Ws; public readonly object SendLock = new object(); }
    readonly HttpListener http = new HttpListener();
    readonly List<Client> clients = new List<Client>();
    readonly AutoResetEvent kick = new AutoResetEvent(false);
    volatile string latest;              // newest sample json; coalesced — freshest wins
    volatile bool running;
    public volatile string StatusExtra = "";  // service state merged into /status (set by Program)
    public long Sent;

    public bool Start() {
        try {
            http.Prefixes.Add("http://127.0.0.1:" + Cfg.Port + "/");
            http.Start();
        } catch (Exception ex) { Log.W("bus start FAILED on port " + Cfg.Port + ": " + ex.Message); return false; }
        running = true;
        var accept = new Thread(AcceptLoop); accept.IsBackground = true; accept.Start();
        var send = new Thread(SendLoop); send.IsBackground = true; send.Start();
        Log.W("bus listening on http://127.0.0.1:" + Cfg.Port + "/ (ws + /status)");
        return true;
    }

    void AcceptLoop() {
        while (running) {
            HttpListenerContext ctx;
            try { ctx = http.GetContext(); } catch { if (!running) return; continue; }
            try {
                if (ctx.Request.IsWebSocketRequest) {
                    var wctx = ctx.AcceptWebSocketAsync(null).Result;
                    var c = new Client { Ws = wctx.WebSocket };
                    lock (clients) { clients.Add(c); }
                    Log.W("bus client connected (" + CountClients() + " total)");
                } else if (ctx.Request.Url.AbsolutePath.StartsWith("/app/park")) {
                    // Per-app park spot (e.g. the Pencil's black rest block). Transient:
                    // memory only, cleared on /app/exit. Body: {"x":0-1,"y":0-1} or {} to clear.
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; ctx.Response.Close(); }
                    else if (ctx.Request.HttpMethod == "POST") {
                        bool okP = false;
                        try {
                            string bodyIn;
                            using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) bodyIn = sr.ReadToEnd();
                            var ser = new JavaScriptSerializer();
                            var d = ser.Deserialize<Dictionary<string, object>>(bodyIn);
                            if (d != null && d.ContainsKey("x") && d.ContainsKey("y")) {
                                double x = Convert.ToDouble(d["x"]), y = Convert.ToDouble(d["y"]);
                                if (x >= 0 && x <= 1 && y >= 0 && y <= 1) {
                                    Cfg.ParkOvX = x; Cfg.ParkOvY = y; okP = true;
                                    Log.W("/app/park override -> " + x.ToString("0.###") + "," + y.ToString("0.###"));
                                }
                            } else { Cfg.ParkOvX = null; Cfg.ParkOvY = null; okP = true; Log.W("/app/park cleared"); }
                        } catch { }
                        byte[] rp = Encoding.UTF8.GetBytes(okP ? "{\"ok\":true}" : "{\"ok\":false}");
                        ctx.Response.StatusCode = okP ? 200 : 400;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = rp.Length;
                        ctx.Response.OutputStream.Write(rp, 0, rp.Length);
                        ctx.Response.Close();
                    } else { ctx.Response.StatusCode = 405; ctx.Response.Close(); }
                } else if (ctx.Request.Url.AbsolutePath.StartsWith("/app/exit")) {
                    // EXIT DOOR (phase 4.1): a web kiosk hands the screen back to the AAC app.
                    // Reply first (the caller dies with its kiosk), then close every one of our
                    // kiosk chromes and foreground Cfg.ForegroundApp (default TD Snap) — no
                    // helper needed (dad's #1 ask).
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; ctx.Response.Close(); }
                    else if (ctx.Request.HttpMethod == "POST") {
                        byte[] r2 = Encoding.UTF8.GetBytes("{\"ok\":true,\"exiting\":true}");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = r2.Length;
                        ctx.Response.OutputStream.Write(r2, 0, r2.Length);
                        ctx.Response.Close();
                        Cfg.ParkOvX = null; Cfg.ParkOvY = null;   // leaving the app restores the corner park
                        Log.W("/app/exit — closing kiosks, handing screen back");
                        System.Threading.ThreadPool.QueueUserWorkItem(delegate { Program.ExitKiosks(); });
                    } else { ctx.Response.StatusCode = 405; ctx.Response.Close(); }
                } else if (ctx.Request.Url.AbsolutePath.StartsWith("/shutdown")) {
                    // Parent-initiated FULL SHUTDOWN (interference testing vs other gaze apps):
                    // reply, then tear down the Tobii Stream Engine connection and exit the
                    // process so the tracker is DEFINITIVELY released (pause only goes inert).
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; ctx.Response.Close(); }
                    else if (ctx.Request.HttpMethod == "POST") {
                        byte[] r = Encoding.UTF8.GetBytes("{\"ok\":true,\"bye\":true}");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = r.Length;
                        ctx.Response.OutputStream.Write(r, 0, r.Length);
                        ctx.Response.Close();     // flush the reply before we exit
                        Log.W("/shutdown requested — releasing tracker + exiting process");
                        Program.Shutdown();       // teardown Tobii, then Environment.Exit(0)
                    } else { ctx.Response.StatusCode = 405; ctx.Response.Close(); }
                } else if (ctx.Request.Url.AbsolutePath.StartsWith("/volcap")) {
                    // Volume cap, two generations on one endpoint (settings-page card):
                    //  LEGACY (unchanged for old clients): the separate 'VolCap' scheduled-task
                    //   watchdog on the gaze devices (board-builder tools/volume-cap). ERAgaze only
                    //   OBSERVES it and relaunches it. GET = status; POST {"on":true|false}
                    //   (OFF is for brief testing only — the task's 30-min self-heal re-arms it).
                    //  INTEGRATED (v2.3): the in-engine cap. GET adds an "integrated" object
                    //   {"enabled","cap","current"}; POST additionally accepts
                    //   {"integratedEnabled":bool,"integratedCap":0-100} which is persisted to
                    //   ERAgaze.json (hot-reload keeps file + engine consistent).
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; ctx.Response.Close(); }
                    else {
                        if (ctx.Request.HttpMethod == "POST") {
                            bool wantOn = true, hasOn = false, hasIntegrated = false;
                            bool intEnabled = Cfg.VolCapEnabled; int intCap = Cfg.VolCapLevel;
                            try {
                                string bodyIn;
                                using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) bodyIn = sr.ReadToEnd();
                                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(bodyIn);
                                if (d != null && d.ContainsKey("on")) { hasOn = true; wantOn = Convert.ToBoolean(d["on"]); }
                                if (d != null && d.ContainsKey("integratedEnabled")) { hasIntegrated = true; intEnabled = Convert.ToBoolean(d["integratedEnabled"]); }
                                if (d != null && d.ContainsKey("integratedCap")) { hasIntegrated = true; intCap = Convert.ToInt32(d["integratedCap"]); }
                            } catch { }
                            if (hasIntegrated) Program.VolCapIntegratedSet(intEnabled, intCap);
                            if (hasOn || !hasIntegrated) {   // legacy path, byte-for-byte old behavior
                                Program.VolCapSet(wantOn);
                                System.Threading.Thread.Sleep(1200);   // let the task/process settle so the reply reflects reality
                            }
                        }
                        byte[] vb = Encoding.UTF8.GetBytes(Program.VolCapStatusJson());
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = vb.Length;
                        ctx.Response.OutputStream.Write(vb, 0, vb.Length);
                        ctx.Response.Close();
                    }
                } else if (ctx.Request.Url.AbsolutePath.StartsWith("/status") ||
                           ctx.Request.Url.AbsolutePath.StartsWith("/config")) {
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; ctx.Response.Close(); }
                    else if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath.StartsWith("/config")) {
                        // settings page writes tuning here; merged into ERAgaze.json (live reload picks it up)
                        string incoming;
                        using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) incoming = sr.ReadToEnd();
                        bool ok = Program.MergeConfig(incoming);
                        byte[] r = Encoding.UTF8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}");
                        ctx.Response.StatusCode = ok ? 200 : 400;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = r.Length; ctx.Response.OutputStream.Write(r, 0, r.Length);
                        ctx.Response.Close();
                    } else {
                        byte[] body = Encoding.UTF8.GetBytes(ctx.Request.Url.AbsolutePath.StartsWith("/config") ? Program.ConfigJson() : BuildStatus());
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = body.Length;
                        ctx.Response.OutputStream.Write(body, 0, body.Length);
                        ctx.Response.Close();
                    }
                } else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
            } catch (Exception ex) { Log.W("bus accept error: " + ex.Message); try { ctx.Response.Close(); } catch { } }
        }
    }

    string BuildStatus() {
        return "{\"device\":\"tobii\",\"service\":\"eragaze\",\"version\":\"2.4\"," +
               "\"paused\":" + (Cfg.Paused ? "true" : "false") + "," +
               "\"connected\":" + (Program.TrackerConnected ? "true" : "false") + "," +
               "\"clients\":" + CountClients() + ",\"sent\":" + Interlocked.Read(ref Sent) +
               (StatusExtra.Length > 0 ? "," + StatusExtra : "") + "}";
    }

    int CountClients() { lock (clients) { return clients.Count; } }

    // Called from the gaze callback thread. Never blocks it: stash + signal.
    public void Publish(string json) { latest = json; kick.Set(); }

    void SendLoop() {
        while (running) {
            kick.WaitOne(250);
            string json = latest; if (json == null) continue; latest = null;
            byte[] buf = Encoding.UTF8.GetBytes(json);
            var seg = new ArraySegment<byte>(buf);
            List<Client> snapshot; lock (clients) { snapshot = new List<Client>(clients); }
            foreach (var c in snapshot) {
                bool drop = false;
                try {
                    lock (c.SendLock) {
                        if (c.Ws.State == WebSocketState.Open) {
                            var t = c.Ws.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None);
                            if (!t.Wait(200)) drop = true; else Interlocked.Increment(ref Sent);
                        } else drop = true;
                    }
                } catch { drop = true; }
                if (drop) { lock (clients) { clients.Remove(c); } try { c.Ws.Abort(); } catch { } Log.W("bus client dropped"); }
            }
        }
    }
}

static class Program {
    static Filtered F = new Filtered();
    static Pipeline pipe = new Pipeline();     // owned by callback thread (gaze) or timer (mouse sim)
    static Overlay Ov;
    static Bus bus;
    static int SW, SH;
    public static volatile bool TrackerConnected;
    static volatile bool pipeReset;            // set by config reload; honored by the owning thread

    // Live Tobii handles for the connected tracker (published by the service loop) so a
    // /shutdown request can release the SAME connection the service opened. IntPtr writes
    // are atomic on both platforms we target.
    static volatile IntPtr curDev = IntPtr.Zero;
    static volatile IntPtr curApi = IntPtr.Zero;

    // Cleanly release the Tobii Stream Engine device + api (the exact teardown the service
    // loop's retry path uses). Safe to call once from the /shutdown handler.
    public static void TeardownTracker() {
        IntPtr d = curDev, a = curApi;
        curDev = IntPtr.Zero; curApi = IntPtr.Zero;
        TrackerConnected = false;
        try { if (d != IntPtr.Zero) { Tobii.tobii_gaze_point_unsubscribe(d); Tobii.tobii_device_destroy(d); } } catch { }
        try { if (a != IntPtr.Zero) Tobii.tobii_api_destroy(a); } catch { }
    }

    // EXIT DOOR (phase 4.1): close every RaeGaze kiosk chrome (matched by command
    // line, never by window title — 7/24 lesson), then foreground TD Snap (UWP).
    // Runs in the interactive session because ERAgaze itself does.
    static string CmdLineOf(int pid) {
        try {
            using (var s = new System.Management.ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                foreach (System.Management.ManagementObject o in s.Get())
                    return (string)(o["CommandLine"] ?? "");
        } catch { }
        return "";
    }
    public static void ExitKiosks() {
        // Our kiosks are identified by their Chrome profile dir living under BaseDir, so
        // match on BaseDir's NAME ("RaeGaze" under the compat default — same behavior).
        string kioskTag = Path.GetFileName(Cfg.BaseDir);
        try {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("chrome")) {
                try {
                    string cmd = CmdLineOf(p.Id);
                    if (cmd.Contains(kioskTag) && cmd.Contains("-profile")) p.Kill();
                } catch { }
            }
        } catch { }
        System.Threading.Thread.Sleep(400);
        // Hand the screen back to the configured app (v2.4: "ForegroundApp" in ERAgaze.json;
        // shell: URIs only, launched via explorer.exe; default = TD Snap's AUMID; "" = none).
        if (!string.IsNullOrEmpty(Cfg.ForegroundApp))
            try { System.Diagnostics.Process.Start("explorer.exe", Cfg.ForegroundApp); } catch { }
        Log.W("kiosks closed" + (string.IsNullOrEmpty(Cfg.ForegroundApp) ? " (no foreground app configured)"
                                                                         : "; foregrounded " + Cfg.ForegroundApp));
    }

    // ---- VolCap status/control for the settings page ----
    // LEGACY half: the separate 'VolCap' scheduled-task watchdog (board-builder
    // tools/volume-cap) — its own task owns running it at logon + 30-min self-heal;
    // ERAgaze just reports state and pushes it back on. Fields installed/running/cap
    // are FROZEN (old settings cards depend on them) until the devices migrate to the
    // integrated cap in a coordinated session.
    // INTEGRATED half (v2.3): the in-engine cap — see VolCapEngine/VolCapWorker above.
    const string VolCapExe = @"C:\Users\Public\Documents\volcap.exe";
    const string VolCapConf = @"C:\Users\Public\Documents\volcap.conf";
    public static string VolCapStatusJson() {
        bool installed = System.IO.File.Exists(VolCapExe);
        bool running = false;
        try { running = System.Diagnostics.Process.GetProcessesByName("volcap").Length > 0; } catch { }
        string cap = "null";
        try {
            if (System.IO.File.Exists(VolCapConf))
                foreach (var line in System.IO.File.ReadAllLines(VolCapConf))
                    if (line.StartsWith("CAP=")) cap = line.Substring(4).Trim();
        } catch { }
        string cur = (Cfg.VolCapEnabled && VolCapEngine.LastPercent >= 0) ? VolCapEngine.LastPercent.ToString() : "null";
        return "{\"installed\":" + (installed ? "true" : "false") +
               ",\"running\":" + (running ? "true" : "false") + ",\"cap\":" + cap +
               ",\"integrated\":{\"enabled\":" + (Cfg.VolCapEnabled ? "true" : "false") +
               ",\"cap\":" + Cfg.VolCapLevel + ",\"current\":" + cur + "}}";
    }
    // POST /volcap {"integratedEnabled","integratedCap"}: persist into ERAgaze.json (the
    // file stays the single source of truth; hot-reload re-reads the same values) and
    // apply immediately so the POST reply already reflects the new state.
    public static void VolCapIntegratedSet(bool enabled, int cap) {
        cap = Math.Max(0, Math.Min(100, cap));
        Cfg.VolCapEnabled = enabled; Cfg.VolCapLevel = cap;
        MergeConfig("{\"VolCapEnabled\":" + (enabled ? "true" : "false") + ",\"VolCapLevel\":" + cap + "}");
        VolCapEngine.Apply();
        Log.W("/volcap integrated -> " + (enabled ? "ON cap=" + cap : "OFF") + " (persisted to ERAgaze.json)");
    }
    public static void VolCapSet(bool on) {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo();
            if (on) { psi.FileName = "schtasks"; psi.Arguments = "/run /tn VolCap"; }
            else    { psi.FileName = "taskkill"; psi.Arguments = "/IM volcap.exe /F"; }
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            var p = System.Diagnostics.Process.Start(psi);
            p.WaitForExit(4000);
            Log.W("/volcap " + (on ? "ON (schtasks /run VolCap)" : "OFF (taskkill volcap)"));
        } catch (Exception ex) { Log.W("/volcap action failed: " + ex.Message); }
    }

    // Full shutdown: release the tracker, then exit the process. Process exit = the OS
    // reclaims the Stream Engine connection outright, so another gaze app can own the tracker.
    public static void Shutdown() {
        try { TeardownTracker(); } catch { }
        try { Log.W("shutdown complete — tracker released, exiting"); } catch { }
        Environment.Exit(0);
    }

    // fixation lock state (timer thread only)
    static bool haveAnchor, locked, parked;
    static double anchorX, anchorY, lockX, lockY, settleX, settleY;
    static DateTime settleStart;
    static int breakRun;                       // consecutive samples beyond BreakRadius
    static long lastSeqSeen;
    static int lastValidTick = Environment.TickCount;
    static DateTime lockedAt;

    // metrics
    static long nSamples, nLoss, nLockFlaps, nParks; static bool inLoss;
    static DateTime lastMetrics = DateTime.Now;

    // dwell-click (opt-in) state
    static bool armed; static double ax, ay; static DateTime dwellStart; static DateTime lastFire = DateTime.MinValue;
    static double firedX, firedY; static bool mustLeave;
    static DateTime flashUntil = DateTime.MinValue;

    // yield-to-human: low-level mouse hook flags any real touch/mouse input
    static DateTime yieldUntil = DateTime.MinValue;
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct MSLL { public int x, y; public uint data, flags, time; public IntPtr extra; }
    static HookProc hookRef; static IntPtr hookH;
    static IntPtr HookCb(int code, IntPtr w, IntPtr l) {
        if (code >= 0) {
            var s = (MSLL)Marshal.PtrToStructure(l, typeof(MSLL));
            if (s.extra != Native.SIG) yieldUntil = DateTime.Now.AddMilliseconds(Cfg.YieldMs); // real finger/mouse
        }
        return CallNextHookEx(hookH, code, w, l);
    }

    // config hot reload
    static DateTime cfgStamp = DateTime.MinValue; static int cfgCheck;

    [STAThread]
    static void Main(string[] args) {
        string bootPath = Path.Combine(Cfg.BaseDir, "boot.txt");   // Cfg.BaseDir resolves --base=/ERAGAZE_BASE here
        try { Directory.CreateDirectory(Cfg.BaseDir); File.WriteAllText(bootPath, "boot " + DateTime.Now + " args=" + string.Join(" ", args)); } catch { }
        try { RealMain(args); }
        catch (Exception ex) { try { File.AppendAllText(bootPath, "\nFATAL " + ex); } catch { } }
    }

    // single-instance: newest launch wins. Every instance writes its PID to the owner
    // file on boot and exits when it stops being the owner — works across users where
    // taskkill can't (her desktop icon vs a SYSTEM/other-user launch).
    static readonly string OwnerPath = Path.Combine(Cfg.BaseDir, "owner.txt");
    static void ClaimOwnership() {
        try { File.WriteAllText(OwnerPath, System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); } catch { }
    }
    static bool StillOwner() {
        try { return File.ReadAllText(OwnerPath).Trim() ==
                     System.Diagnostics.Process.GetCurrentProcess().Id.ToString(); }
        catch { return true; }
    }

    static void RealMain(string[] args) {
        Cfg.WriteDefault();
        Cfg.LoadFile();
        ClaimOwnership();
        try { cfgStamp = File.GetLastWriteTimeUtc(Cfg.ConfigPath); } catch { }
        foreach (var a in args) {   // CLI overrides the file for this run
            if (a.StartsWith("--source=")) Cfg.Source = a.Substring(9).ToLowerInvariant();
            else if (a.StartsWith("--dwellclick=")) Cfg.DwellClick = a.Substring(13).ToLowerInvariant() == "on";
            else if (a.StartsWith("--dwell=")) int.TryParse(a.Substring(8), out Cfg.DwellMs);
            else if (a.StartsWith("--yield=")) int.TryParse(a.Substring(8), out Cfg.YieldMs);
            else if (a.StartsWith("--port=")) int.TryParse(a.Substring(7), out Cfg.Port);
            else if (a.StartsWith("--mincutoff=")) double.TryParse(a.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out Cfg.MinCutoff);
            else if (a.StartsWith("--beta=")) double.TryParse(a.Substring(7), NumberStyles.Float, CultureInfo.InvariantCulture, out Cfg.Beta);
            else if (a.StartsWith("--lock=")) int.TryParse(a.Substring(7), out Cfg.LockRadius);
            else if (a.StartsWith("--median=")) int.TryParse(a.Substring(9), out Cfg.Median);
            else if (a.StartsWith("--base=")) { }   // consumed by Cfg.ResolveBaseDir() before anything else ran
            else if (a == "--nobus") Cfg.Bus = false;
            else if (a == "--force") Cfg.Force = true;
        }
        Native.SetProcessDPIAware();
        SW = Screen.PrimaryScreen.Bounds.Width; SH = Screen.PrimaryScreen.Bounds.Height;
        pipe.Reset();
        Log.W("RaeGaze 2.0 start source=" + Cfg.Source + " screen=" + SW + "x" + SH +
              " median=" + Cfg.Median + " mincutoff=" + Cfg.MinCutoff + " beta=" + Cfg.Beta +
              " lock=" + Cfg.LockRadius + "/" + Cfg.LockOnMs + "ms break=" + Cfg.BreakRadius + "x" + Cfg.BreakSamples +
              " grace=" + Cfg.GraceMs + " park=" + (Cfg.ParkOnLoss ? Cfg.ParkAfterMs + "ms" : "off") +
              " bus=" + (Cfg.Bus ? "" + Cfg.Port : "off") +
              " volcap=" + (Cfg.VolCapEnabled ? "" + Cfg.VolCapLevel : "off"));

        VolCapEngine.Apply();   // integrated volume cap: inert unless ERAgaze.json opted in
        if (Cfg.Bus) { bus = new Bus(); if (!bus.Start()) bus = null; }
        if (Cfg.Source == "gaze") StartTobiiService();

        Ov = new Overlay();
        hookRef = HookCb;
        hookH = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, hookRef, GetModuleHandle(null), 0);
        if (hookH == IntPtr.Zero) { hookH = SetWindowsHookEx(14, hookRef, IntPtr.Zero, 0); }
        Log.W("mouse hook " + (hookH != IntPtr.Zero ? "installed" : "FAILED err=" + Marshal.GetLastWin32Error()));
        var t = new System.Windows.Forms.Timer { Interval = 16 };
        t.Tick += Tick;
        Ov.Load += (s, e) => { Ov.SetFrame(Blank(), -100, -100); t.Start(); };
        Application.Run(Ov);
    }

    static Bitmap Blank() { return new Bitmap(2, 2, PixelFormat.Format32bppArgb); }

    // ---- tracker service loop: connect, stream, and on any failure retry forever ----
    static void StartTobiiService() {
        var th = new Thread(() => {
            while (true) {
                IntPtr api = IntPtr.Zero, dev = IntPtr.Zero;
                Tobii.GazeCb cb = null;
                try {
                    var ver = new Tobii.Ver(); Tobii.tobii_get_api_version(ref ver);
                    if (Tobii.tobii_api_create(out api, IntPtr.Zero, IntPtr.Zero) != 0) { Log.W("api_create fail; retrying"); goto retry; }
                    string url = null;
                    Tobii.UrlRecv recv = (u, x) => { if (url == null) url = u; };
                    Tobii.tobii_enumerate_local_device_urls(api, recv, IntPtr.Zero);
                    GC.KeepAlive(recv);
                    if (url == null) { Log.W("no tracker found; retrying in 3s"); goto retry; }
                    int err = ver.major >= 4 ? Tobii.device_create4(api, url, 1 /*INTERACTIVE*/, out dev) : Tobii.device_create3(api, url, out dev);
                    if (err != 0) { Log.W("device_create err " + err + "; retrying"); goto retry; }

                    cb = GazeCallback;   // static delegate kept alive below
                    if (Tobii.tobii_gaze_point_subscribe(dev, cb, IntPtr.Zero) != 0) { Log.W("subscribe fail; retrying"); goto retry; }
                    curApi = api; curDev = dev;   // publish live handles so /shutdown can release this connection
                    TrackerConnected = true;
                    Log.W("tracker " + url + " api " + ver.major + "." + ver.minor + " — streaming (device calibration in effect)");
                    int consecutiveErrs = 0;
                    while (true) {
                        int perr = Tobii.tobii_device_process_callbacks(dev);
                        if (perr != 0) { if (++consecutiveErrs > 20) { Log.W("process_callbacks err " + perr + " x" + consecutiveErrs + "; reconnecting"); break; } }
                        else consecutiveErrs = 0;
                        Thread.Sleep(5);
                        GC.KeepAlive(cb);
                    }
                } catch (Exception ex) { Log.W("tobii service ex: " + ex.Message); }
            retry:
                TrackerConnected = false;
                curDev = IntPtr.Zero; curApi = IntPtr.Zero;   // handles no longer valid for /shutdown
                try { if (dev != IntPtr.Zero) { Tobii.tobii_gaze_point_unsubscribe(dev); Tobii.tobii_device_destroy(dev); } } catch { }
                try { if (api != IntPtr.Zero) Tobii.tobii_api_destroy(api); } catch { }
                Thread.Sleep(3000);
            }
        });
        th.IsBackground = true; th.Start();
    }

    // Runs on the Tobii callback thread: filter in PIXEL space with DEVICE time, then
    // publish to the shared state + gaze bus. Never throws into native code.
    static void GazeCallback(ref Tobii.GazePoint gp, IntPtr u) {
        try {
            if (Cfg.Paused) return;   // parent test pause: no filtering, no shared-state update, no bus samples
            Interlocked.Increment(ref nSamples);
            bool valid = gp.validity == 1;
            double x01 = 0, y01 = 0, px = 0, py = 0;
            if (valid) {
                if (pipeReset) { pipe.Reset(); pipeReset = false; }
                double t = gp.ts / 1000000.0;                       // device µs -> s
                pipe.Filter(gp.x * (SW - 1), gp.y * (SH - 1), t, out px, out py);
                px = Math.Max(0, Math.Min(SW - 1, px)); py = Math.Max(0, Math.Min(SH - 1, py));
                x01 = px / (SW - 1); y01 = py / (SH - 1);
            }
            lock (F.Lock) {
                F.Valid = valid;
                if (valid) { F.Px = px; F.Py = py; F.X01 = x01; F.Y01 = y01; F.Seq++; }
                F.StampTick = Environment.TickCount;
            }
            if (bus != null) bus.Publish(SampleJson(valid, x01, y01, px, py, gp.ts));
        } catch (Exception ex) { Log.W("gaze cb ex: " + ex.Message); }
    }

    static string SampleJson(bool valid, double x01, double y01, double px, double py, long ts) {
        var ci = CultureInfo.InvariantCulture;
        // Aivota sidecar contract first; extra fields after (consumers may ignore them).
        return "{\"gazePoint\":{\"x\":" + x01.ToString("F5", ci) + ",\"y\":" + y01.ToString("F5", ci) + "}," +
               "\"validity\":" + (valid ? 1 : 0) + ",\"timestamp\":" + ts +
               ",\"px\":" + px.ToString("F1", ci) + ",\"py\":" + py.ToString("F1", ci) +
               ",\"locked\":" + (locked ? "true" : "false") + "}";
    }

    // mouse-simulation source: filter here (timer thread owns the pipeline in this mode)
    static void ReadMouseSim() {
        if (pipeReset) { pipe.Reset(); pipeReset = false; }
        Native.POINT p; Native.GetCursorPos(out p);
        double t = Environment.TickCount / 1000.0, px, py;
        pipe.Filter(p.X, p.Y, t, out px, out py);
        lock (F.Lock) { F.Valid = true; F.Px = px; F.Py = py; F.X01 = px / (SW - 1); F.Y01 = py / (SH - 1); F.Seq++; F.StampTick = Environment.TickCount; }
        if (bus != null) bus.Publish(SampleJson(true, px / (SW - 1), py / (SH - 1), px, py, (long)Environment.TickCount * 1000));
    }

    static bool Suppressed() {
        if (Cfg.Force) return false;
        string p = Native.ForegroundProc();
        foreach (var d in Cfg.Deny) if (p.Contains(d)) return true;
        return false;
    }

    // Settings page POSTs a partial config here; we merge it into ERAgaze.json.
    // The existing file-watch live-reload then applies it within ~2s. Thread-safe
    // enough for a low-rate settings page (last write wins).
    public static bool MergeConfig(string incomingJson) {
        try {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var incoming = ser.Deserialize<Dictionary<string, object>>(incomingJson);
            if (incoming == null) return false;
            Dictionary<string, object> cur;
            try { cur = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(Cfg.ConfigPath)); }
            catch { cur = new Dictionary<string, object>(); }
            if (cur == null) cur = new Dictionary<string, object>();
            foreach (var kv in incoming) cur[kv.Key] = kv.Value;   // only whitelisted keys reach us from the page
            File.WriteAllText(Cfg.ConfigPath, ser.Serialize(cur));
            Log.W("config merged via /config: " + incomingJson);
            return true;
        } catch (Exception ex) { Log.W("MergeConfig failed: " + ex.Message); return false; }
    }
    public static string ConfigJson() {
        var ci = CultureInfo.InvariantCulture;
        // serializer for the v2.4 string/array keys (proper JSON escaping of backslashes)
        var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
        return "{\"minCutoff\":" + Cfg.MinCutoff.ToString(ci) + ",\"beta\":" + Cfg.Beta.ToString(ci) +
               ",\"lockRadius\":" + Cfg.LockRadius + ",\"lockOnMs\":" + Cfg.LockOnMs +
               ",\"breakRadius\":" + Cfg.BreakRadius + ",\"graceMs\":" + Cfg.GraceMs +
               ",\"parkAfterMs\":" + Cfg.ParkAfterMs + ",\"dwellClick\":" + (Cfg.DwellClick ? "true" : "false") +
               ",\"paused\":" + (Cfg.Paused ? "true" : "false") +
               ",\"radius\":" + Cfg.Radius +
               ",\"ForegroundApp\":" + ser.Serialize(Cfg.ForegroundApp) +
               ",\"DenyApps\":" + ser.Serialize(Cfg.Deny) + "}";
    }

    static void MaybeReloadConfig() {
        if (++cfgCheck % 120 != 0) return;   // every ~2s at 16ms ticks
        if (!StillOwner()) { Log.W("newer instance took over; exiting"); Application.Exit(); return; }
        try {
            var st = File.GetLastWriteTimeUtc(Cfg.ConfigPath);
            if (st != cfgStamp) {
                cfgStamp = st;
                if (Cfg.LoadFile()) { pipeReset = true; VolCapEngine.Apply(); Log.W("config reloaded: mincutoff=" + Cfg.MinCutoff + " beta=" + Cfg.Beta + " lock=" + Cfg.LockRadius + "/" + Cfg.LockOnMs + " break=" + Cfg.BreakRadius + "x" + Cfg.BreakSamples + " grace=" + Cfg.GraceMs + " dwellclick=" + Cfg.DwellClick + " volcap=" + (Cfg.VolCapEnabled ? "" + Cfg.VolCapLevel : "off")); }
            }
        } catch { }
    }

    static void Metrics(DateTime now) {
        if ((now - lastMetrics).TotalSeconds < 10) return;
        lastMetrics = now;
        Log.W("METRICS samples=" + Interlocked.Read(ref nSamples) + " loss=" + nLoss + " lockFlaps=" + nLockFlaps +
              " parks=" + nParks + " locked=" + (locked ? 1 : 0) + " parked=" + (parked ? 1 : 0) +
              " busClients=" + (bus != null ? "y" : "n") + " fg=" + Native.ForegroundProc());
        if (bus != null) bus.StatusExtra =
            "\"suppressed\":" + (Suppressed() ? "true" : "false") + ",\"locked\":" + (locked ? "true" : "false") +
            ",\"parked\":" + (parked ? "true" : "false") + ",\"loss\":" + nLoss + ",\"lockFlaps\":" + nLockFlaps +
            ",\"screen\":{\"w\":" + SW + ",\"h\":" + SH + "}";
    }

    static int topmostTick;   // counts Tick calls; re-assert ring z-order ~every 480ms
    static void Tick(object s, EventArgs e) {
        var now = DateTime.Now;
        MaybeReloadConfig();
        Metrics(now);

        // Keep the ring above full-screen kiosks (cheap no-op when already topmost).
        // Runs unconditionally, before any state early-return, so the ring is on top the
        // moment it's next drawn even if a kiosk grabbed the foreground meanwhile.
        if (++topmostTick >= 30) { topmostTick = 0; Ov.ReassertTopmost(); }

        // Parent test pause: fully inert. Hide the ring, never move the cursor, clear all
        // lock/dwell state. The gaze bus stops streaming (see GazeCallback) but the HTTP
        // server keeps serving /status + /config so the settings page can un-pause.
        if (Cfg.Paused) { Ov.SetFrame(Blank(), -100, -100); haveAnchor = false; locked = false; armed = false; parked = false; breakRun = 0; return; }

        // Step aside for TD Snap / Tobii UIs (their own gaze runs there). Bus keeps streaming.
        if (Suppressed()) { Ov.SetFrame(Blank(), -100, -100); haveAnchor = false; locked = false; armed = false; return; }

        if (Cfg.Source == "mouse") ReadMouseSim();

        // read the latest filtered sample
        bool valid; double px, py; long seq; int stamp;
        lock (F.Lock) { valid = F.Valid; px = F.Px; py = F.Py; seq = F.Seq; stamp = F.StampTick; }
        bool fresh = (Environment.TickCount - stamp) < Cfg.FreshMs && valid;

        // ---- TRACK LOSS (blink / look-away / head turn) ----
        // Brief loss: HOLD lock + cursor (forgiveness — a blink never resets progress).
        // Sustained loss: unlock, disarm, and park the cursor at the rest corner so a
        // frozen pointer can never sit on a button while an app's hover-dwell fills.
        if (!fresh) {
            if (!inLoss) { inLoss = true; nLoss++; lastValidTick = Environment.TickCount; }
            int lossMs = Environment.TickCount - lastValidTick;
            if (lossMs < Cfg.GraceMs) { RenderState(now, 0, true); return; }     // hold, shown as paused
            locked = false; armed = false; haveAnchor = false; breakRun = 0;
            bool yielding0 = now < yieldUntil;
            if (Cfg.ParkOnLoss && !parked && lossMs >= Cfg.ParkAfterMs && !yielding0 && Cfg.Source == "gaze") {
                double pkx = Cfg.ParkOvX ?? Cfg.ParkX01, pky = Cfg.ParkOvY ?? Cfg.ParkY01;
                Native.Move((int)(pkx * (SW - 1)), (int)(pky * (SH - 1)));
                parked = true; nParks++;
                Log.W("track loss " + lossMs + "ms -> parked cursor");
            }
            if (parked) Ov.SetFrame(Blank(), -100, -100); else RenderState(now, 0, true);
            return;
        }
        if (inLoss) { inLoss = false; }
        lastValidTick = Environment.TickCount;
        if (parked) { parked = false; haveAnchor = false; }   // eyes back: resume cleanly at gaze

        // ---- fixation lock (two-phase, sustained exit) ----
        bool newSample = seq != lastSeqSeen; lastSeqSeen = seq;
        if (!haveAnchor) { anchorX = settleX = px; anchorY = settleY = py; settleStart = now; haveAnchor = true; locked = false; breakRun = 0; }
        if (locked && newSample) {
            // unlock only on a SUSTAINED exit: single flicks/blink artifacts don't count
            if (Dist(px, py, lockX, lockY) > Cfg.BreakRadius) {
                if (++breakRun >= Cfg.BreakSamples) {
                    locked = false; breakRun = 0;
                    if ((now - lockedAt).TotalMilliseconds < 300) nLockFlaps++;  // metric: lock didn't hold
                }
            } else breakRun = 0;
        }
        if (!locked) {
            anchorX = px; anchorY = py;                        // follow the smoothed gaze
            if (Dist(px, py, settleX, settleY) > Cfg.LockRadius) { settleX = px; settleY = py; settleStart = now; }
            else if ((now - settleStart).TotalMilliseconds >= Cfg.LockOnMs) { locked = true; lockedAt = now; lockX = anchorX = px; lockY = anchorY = py; breakRun = 0; }
        } else { anchorX = lockX; anchorY = lockY; }           // frozen on the fixation
        int cx = (int)Math.Round(anchorX), cy = (int)Math.Round(anchorY);

        // ---- yield to a human, then drive the cursor ----
        bool yielding = now < yieldUntil;
        if (yielding) { Ov.SetFrame(Blank(), -100, -100); armed = false; return; }  // hands off, ring hidden
        if (Cfg.Source == "gaze") Native.Move(cx, cy);

        // ---- dwell-to-click: OPT-IN only (caregiver/QA surfaces). Loss above disarms it. ----
        double progress = 0;
        if (Cfg.DwellClick) {
            if (mustLeave) { if (Dist(cx, cy, firedX, firedY) > Cfg.ReArm) mustLeave = false; }
            if (!mustLeave) {
                if (!armed) { armed = true; ax = cx; ay = cy; dwellStart = now; }
                else if (Dist(cx, cy, ax, ay) <= Cfg.Tol) {
                    ax += 0.15 * (cx - ax); ay += 0.15 * (cy - ay);
                    progress = (now - dwellStart).TotalMilliseconds / Cfg.DwellMs;
                    if (progress >= 1.0 && (now - lastFire).TotalMilliseconds > Cfg.CooldownMs) {
                        Native.Click((int)ax, (int)ay);
                        lastFire = now; flashUntil = now.AddMilliseconds(220);
                        firedX = ax; firedY = ay; mustLeave = true; armed = false; progress = 0;
                        Log.W("click " + (int)ax + "," + (int)ay);
                    }
                } else { ax = cx; ay = cy; dwellStart = now; }
            }
        }

        RenderState(now, Math.Min(progress, 1.0), false);
    }

    static double Dist(double x1, double y1, double x2, double y2) { double dx = x1 - x2, dy = y1 - y2; return Math.Sqrt(dx * dx + dy * dy); }

    // Ring states: normal follow (white), locked fixation (teal tint), paused/loss (gray, no dot).
    static void RenderState(DateTime now, double progress, bool paused) {
        int cx = (int)Math.Round(anchorX), cy = (int)Math.Round(anchorY);
        if (!haveAnchor) { Ov.SetFrame(Blank(), -100, -100); return; }
        int R = Cfg.Radius, pad = 12, size = 2 * (R + pad);
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float c = size / 2f;
            var ring = new RectangleF(c - R, c - R, 2 * R, 2 * R);
            using (var pen = new Pen(Color.FromArgb(90, 10, 20, 24), 7)) g.DrawEllipse(pen, ring); // contrast halo
            if (paused) {
                using (var pen = new Pen(Color.FromArgb(110, 190, 196, 198), 3.2f)) g.DrawEllipse(pen, ring);
            } else if (locked) {
                using (var pen = new Pen(Color.FromArgb(235, 122, 205, 216), 3.2f)) g.DrawEllipse(pen, ring);
                using (var br = new SolidBrush(Color.FromArgb(200, 15, 124, 138))) g.FillEllipse(br, c - 3.5f, c - 3.5f, 7, 7);
            } else {
                using (var pen = new Pen(Color.FromArgb(235, 245, 250, 250), 3.2f)) g.DrawEllipse(pen, ring);
                using (var br = new SolidBrush(Color.FromArgb(180, 15, 124, 138))) g.FillEllipse(br, c - 3.5f, c - 3.5f, 7, 7);
            }
            if (progress > 0) {
                using (var pen = new Pen(Color.FromArgb(255, 15, 124, 138), 5.2f)) {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, ring, -90, (float)(360 * progress));
                }
            }
            if (now < flashUntil) {
                double k = 1 - (flashUntil - now).TotalMilliseconds / 220.0;
                int alpha = (int)(160 * (1 - k)); float rr = (float)(R + 2 + k * (pad - 2));
                using (var pen = new Pen(Color.FromArgb(Math.Max(0, alpha), 15, 124, 138), 3f))
                    g.DrawEllipse(pen, c - rr, c - rr, 2 * rr, 2 * rr);
            }
            Ov.SetFrame(bmp, cx - size / 2, cy - size / 2);
        }
    }
}
