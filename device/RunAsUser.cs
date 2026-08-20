// RunAsUser — launch a process as the interactive console user (td13l), from a SYSTEM
// context (via PsExec -s), on the interactive desktop. Bypasses the flaky Task Scheduler.
// Usage (as SYSTEM):  RunAsUser.exe <full exe path> [args...]
using System;
using System.Runtime.InteropServices;

static class RunAsUser {
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint sid, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, uint len, out uint retLen);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr h, uint access, IntPtr attr, int imp, int type, out IntPtr dup);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string res1, desktop, title; public int x, y, xs, ys, xc, yc, fill, flags; public short show, res2; public IntPtr res3, si, so, se; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hp, ht; public int pid, tid; }

    static void Main(string[] a) {
        if (a.Length == 0) { Console.WriteLine("usage: RunAsUser <exe> [args]"); return; }
        string cmd = "\"" + string.Join(" ", a) + "\"";   // whole thing is the (possibly spaced) exe path
        uint sid = WTSGetActiveConsoleSessionId();
        IntPtr tok;
        if (!WTSQueryUserToken(sid, out tok)) { Console.WriteLine("WTSQueryUserToken err " + Marshal.GetLastWin32Error()); return; }
        // her account is admin -> WTSQueryUserToken hands back the UAC-filtered token; get the
        // full (elevated) linked token so we can start apps that require elevation.
        IntPtr src = tok;
        IntPtr buf = Marshal.AllocHGlobal(IntPtr.Size); uint rl;
        if (GetTokenInformation(tok, 19 /*TokenLinkedToken*/, buf, (uint)IntPtr.Size, out rl)) {
            IntPtr linked = Marshal.ReadIntPtr(buf);
            if (linked != IntPtr.Zero) src = linked;
        }
        Marshal.FreeHGlobal(buf);
        IntPtr dup;
        if (!DuplicateTokenEx(src, 0x02000000 /*MAXIMUM_ALLOWED*/, IntPtr.Zero, 2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out dup)) { Console.WriteLine("dup err " + Marshal.GetLastWin32Error()); return; }
        IntPtr env; CreateEnvironmentBlock(out env, dup, false);
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(STARTUPINFO)); si.desktop = @"winsta0\default";
        PROCESS_INFORMATION pi;
        bool ok = CreateProcessAsUser(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x00000400 /*CREATE_UNICODE_ENVIRONMENT*/, env, null, ref si, out pi);
        string msg = "cmd=" + cmd + " usedLinked=" + (src != tok) + " CreateProcessAsUser=" + ok + " pid=" + (ok ? pi.pid : 0) + " err=" + Marshal.GetLastWin32Error();
        Console.WriteLine(msg);
        try { System.IO.File.WriteAllText(@"C:\Users\Public\RaeGaze\rau_result.txt", msg); } catch { }
    }
}
