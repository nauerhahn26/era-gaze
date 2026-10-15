// VolCap — clamp Windows master volume at CAP (volcap.conf, default 55).
// Event-driven (IAudioEndpointVolumeCallback = clamps in <100ms) + 3s safety poll
// that also re-registers when the default audio endpoint changes.
// Volume-down and Mute are never touched (dad's ruling 8/14: Ellie may mute).
// Deploy: tools/volume-cap/deploy-volcap.sh — eye-gaze devices ONLY, never a tablet.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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
    int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
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

class ClampCallback : IAudioEndpointVolumeCallback {
    public int OnNotify(IntPtr pNotifyData) {
        try {
            VolNotifyData d = (VolNotifyData)Marshal.PtrToStructure(pNotifyData, typeof(VolNotifyData));
            if (d.guidEventContext != VolCap.OurContext && d.fMasterVolume > VolCap.CapScalar + 0.004f)
                VolCap.Clamp(d.fMasterVolume, "event");
        } catch { }
        return 0;
    }
}

static class VolCap {
    public static Guid OurContext = new Guid("7e11e0c5-0d5a-4a5e-9a55-c4a55e55ca55");
    public static float CapScalar = 0.55f;
    static string baseDir = @"C:\Users\Public\Documents";
    static IAudioEndpointVolume vol; static string devId = null; static ClampCallback cb = new ClampCallback();

    public static void Clamp(float from, string src) {
        try {
            IAudioEndpointVolume v = vol; if (v == null) return;
            v.SetMasterVolumeLevelScalar(CapScalar, ref OurContext);
            Log(string.Format("clamp {0:F0} -> {1:F0} ({2})", from * 100, CapScalar * 100, src));
        } catch (Exception e) { Log("clamp FAILED: " + e.Message); }
    }

    static void Log(string m) {
        try {
            string p = Path.Combine(baseDir, "volcap.log");
            FileInfo fi = new FileInfo(p);
            if (fi.Exists && fi.Length > 512 * 1024) File.WriteAllText(p, "");
            File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + "\r\n");
        } catch { }
    }

    static void LoadCap() {
        try {
            string p = Path.Combine(baseDir, "volcap.conf");
            if (File.Exists(p))
                foreach (string line in File.ReadAllLines(p))
                    if (line.StartsWith("CAP=")) CapScalar = int.Parse(line.Substring(4).Trim()) / 100f;
        } catch (Exception e) { Log("conf read failed, keeping " + (CapScalar * 100) + ": " + e.Message); }
    }

    static void Attach() {
        IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice dev; Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out dev));
        string id; dev.GetId(out id);
        if (id == devId && vol != null) return;
        if (vol != null) { try { vol.UnregisterControlChangeNotify(cb); } catch { } }
        Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
        vol = (IAudioEndpointVolume)o; devId = id;
        vol.RegisterControlChangeNotify(cb);
        Log("attached endpoint " + id + " cap=" + (CapScalar * 100));
    }

    static void Main() {
        bool created;
        Mutex mx = new Mutex(true, "Global\\VolCapSingleton", out created);
        if (!created) return;
        LoadCap();
        Log("start cap=" + (CapScalar * 100));
        DateTime beat = DateTime.Now;
        while (true) {
            try {
                Attach();
                float f; vol.GetMasterVolumeLevelScalar(out f);
                if (f > CapScalar + 0.004f) Clamp(f, "poll");
                if ((DateTime.Now - beat).TotalMinutes >= 30) { beat = DateTime.Now; Log(string.Format("heartbeat vol={0:F0}", f * 100)); }
            } catch (Exception e) { Log("poll error: " + e.Message); vol = null; devId = null; }
            Thread.Sleep(3000);
        }
    }
}
