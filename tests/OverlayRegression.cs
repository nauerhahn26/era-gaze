using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Forms;

// Compiled beside the actual engine source, with a separate entry point.
// The fault build replaces only GetHbitmap's call site, so the exception enters
// the production resource-cleanup and recovery path. No tracker or bus starts.
static class OverlayRegression {
    static bool fail;
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    public static IntPtr GetHbitmap(Bitmap bitmap) {
        if (fail) throw new ExternalException("injected display conversion failure");
        return bitmap.GetHbitmap(Color.FromArgb(0));
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    [STAThread] static int Main(string[] args) {
        try {
            string data = Path.Combine(Path.GetDirectoryName(args[0]), "isolated-data");
            Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("ERAGAZE_BASE", data);
            using (var overlay = new Overlay())
            using (var frame = new Bitmap(64, 64, PixelFormat.Format32bppArgb)) {
                using (var g = Graphics.FromImage(frame)) g.Clear(Color.Teal);
                Check(overlay.SetFrame(frame, 10, 10), "initial frame");
                Check(overlay.Visible, "frame becomes visible");
                int before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                if (args.Length > 1 && args[1] == "fault") {
                    fail = true;
                    for (int i = 0; i < 100; i++) {
                        Check(!overlay.SetFrame(frame, 10, 10), "failure reported");
                        Check(!overlay.Visible, "stale overlay hidden");
                    }
                    // Watch surfaces must retry an identical frame after failure;
                    // caching its dirty key before success would keep it hidden.
                    var flags = BindingFlags.Static | BindingFlags.NonPublic;
                    typeof(Watch).GetField("wakeOv", flags).SetValue(null, overlay);
                    var drawWake = typeof(Watch).GetMethod("DrawWake", flags);
                    drawWake.Invoke(null, new object[] { new Rectangle(10, 10, 120, 120), 0.0 });
                    fail = false;
                    drawWake.Invoke(null, new object[] { new Rectangle(10, 10, 120, 120), 0.0 });
                    Check(overlay.Visible, "same wake frame retried after failure");
                    typeof(Watch).GetField("stripOv", flags).SetValue(null, overlay);
                    var tiles = new Rectangle[6]; tiles[0] = new Rectangle(10, 10, 160, 160);
                    var drawStrip = typeof(Watch).GetMethod("DrawStrip", flags);
                    fail = true;
                    drawStrip.Invoke(null, new object[] { tiles, new Rectangle(10, 10, 180, 180), -1, 0.0 });
                    Check(!overlay.Visible, "failed strip hidden");
                    fail = false;
                    drawStrip.Invoke(null, new object[] { tiles, new Rectangle(10, 10, 180, 180), -1, 0.0 });
                    Check(overlay.Visible, "same strip frame retried after failure");
                    Check(overlay.SetFrame(frame, 10, 10), "recovery after display failure");
                    Check(overlay.Visible, "overlay visible after recovery");
                }
                for (int i = 0; i < 1000; i++) {
                    overlay.HideFrame();
                    overlay.HideFrame();
                    Check(!overlay.Visible, "hide is idempotent");
                    Check(overlay.SetFrame(frame, 10, 10), "frame after hide");
                    Application.DoEvents();
                }
                int after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                Check(after <= before + 2, "GDI resources grew: " + before + " -> " + after);
                File.WriteAllText(args[0], "PASS: 10000 hides + 200 hide/draw cycles; GDI " + before + " -> " + after + "; fault=" + (args.Length > 1));
            }
            return 0;
        } catch (Exception ex) { File.WriteAllText(args[0], "FAIL: " + ex); return 1; }
    }
}
