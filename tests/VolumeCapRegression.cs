using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

static class VolumeCapRegression {
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static Dictionary<string, object> Device() {
        var status = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Program.VolCapStatusJson());
        return (Dictionary<string, object>)status["device"];
    }
    static void State(bool enabled, int cap) {
        var d = Device();
        Check((bool)d["enabled"] == enabled && (int)d["cap"] == cap, "Wrong saved device state");
    }
    static Process Watchdog() {
        var process = Process.Start(new ProcessStartInfo(Path.Combine(Cfg.BaseDir, "volcap.exe")) { UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(600);
        Check(!process.HasExited, "Test watchdog did not start");
        return process;
    }
    static int Main(string[] args) {
        try {
            if (Array.IndexOf(args, "--read-off") >= 0) {
                Check(Cfg.LoadFile(), "Could not reload saved settings in a fresh process");
                State(false, 70);
                Check(Cfg.VolCapManaged, "Migration ownership did not persist");
                return 0;
            }
            Directory.CreateDirectory(Cfg.BaseDir);
            Check(Cfg.VolCapLevel == 45 && !Cfg.VolCapEnabled, "Default cap must be 45; new devices opt in");
            File.WriteAllText(Path.Combine(Cfg.BaseDir, "volcap.conf"), "CAP=45\r\n");
            using (var old = Watchdog()) {
                State(true, 45);
                Program.VolCapDeviceSet(true, 65);
                Check(old.WaitForExit(4000), "Old 45% watchdog still limits the speaker");
            }
            Check(File.ReadAllText(Path.Combine(Cfg.BaseDir, "volcap.conf")).Trim() == "CAP=100", "Scheduled watchdog can reimpose old cap");
            State(true, 65);
            Program.VolCapDeviceSet(true, 25); State(true, 25);
            Program.VolCapDeviceSet(true, 90); State(true, 90);
            Program.VolCapDeviceSet(false, 70); State(false, 70);
            using (var fresh = Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                       "--read-off --base=\"" + Cfg.BaseDir + "\"") { UseShellExecute = false, CreateNoWindow = true })) {
                Check(fresh.WaitForExit(10000) && fresh.ExitCode == 0, "Off/cap did not survive process restart");
            }
            using (var restarted = Watchdog()) {
                State(false, 70); // A task restart must not change the saved On/Off state.
                Program.VolCapDeviceSet(true, 80);
                Check(restarted.WaitForExit(4000), "Restarted watchdog did not retire");
                State(true, 80);
            }
            bool enabled; int cap;
            foreach (var value in new object[] { -1, 101, 1.5, "45", null }) {
                var request = new Dictionary<string, object> { { "deviceEnabled", true }, { "deviceCap", value } };
                Check(!Program.TryDeviceVolumeRequest(request, out enabled, out cap), "Invalid percentage accepted");
            }
            Check(Program.TryDeviceVolumeRequest(new Dictionary<string, object> { { "deviceEnabled", false }, { "deviceCap", 0 } }, out enabled, out cap), "Zero cap rejected");
            string config = Cfg.ConfigPath;
            Cfg.ConfigPath = Path.Combine(Cfg.BaseDir, "missing-directory", "config.json");
            bool failed = false;
            try { Program.VolCapDeviceSet(false, 10); } catch (IOException) { failed = true; }
            Cfg.ConfigPath = config;
            Check(failed, "Failed persistence reported success");
            State(true, 80);
            Program.VolCapDeviceSet(false, 80);
            Console.WriteLine("PASS: default 45, legacy takeover, increases, decreases, Off/restart, task restart, validation and failed persistence");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { Cfg.VolCapEnabled = false; VolCapEngine.Apply(); }
    }
}
