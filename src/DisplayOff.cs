using System;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Night screen mode (toggle).
//
// Default ("soft") mode: brightness to 0 + a black full-screen window on top, and the
// system is told to keep both itself and the display awake. The panel is never powered
// off, so Modern Standby (S0) never kicks in and the session is never locked - no
// password on return, and touching the touchpad produces no flash at all.
//
// "/hard" mode: the old behaviour - panel power off, re-applied every 120 ms. Darker,
// but on a Modern Standby machine it makes Windows lock the session.
//
// Exit: second run, Ctrl+Alt+D, Esc-Esc-Esc, or timeout.
// Args: [delay_ms] [/timeout:SECONDS] [/hard]
static class DisplayOff
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam,
        IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[] pHandles, bool bWaitAll,
        uint dwMilliseconds, uint dwWakeMask);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);

    const uint WM_SYSCOMMAND = 0x0112;
    const int SC_MONITORPOWER = 0xF170;
    const int MONITOR_OFF = 2, MONITOR_ON = -1;
    static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
    const uint SMTO_ABORTIFHUNG = 0x0002;

    const int WH_KEYBOARD_LL = 13;
    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint WM_QUIT = 0x0012;
    const uint PM_REMOVE = 1, QS_ALLINPUT = 0x04FF;
    const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;

    const int HARD_REFRESH_MS = 120;   // how often the panel is put back to sleep in /hard
    const int SOFT_REFRESH_MS = 2000;  // how often the black window is pushed back on top

    const string MUTEX_NAME = "Local\\DisplayOffNightMode";
    const string EVENT_NAME = "Local\\DisplayOffNightStop";
    static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "displayoff.log");

    static EventWaitHandle stopEvent;
    static bool ctrlDown, altDown;
    static int escCount, escFirstTick;
    static HookProc kbProc;

    static void Log(string s)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine); }
        catch { }
    }

    static void MonitorPower(int state)
    {
        IntPtr r;
        SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER,
            (IntPtr)state, SMTO_ABORTIFHUNG, 1000, out r);
    }

    // WMI calls here can hang for a minute after applying the change, so every one of
    // them runs on a throwaway background thread we only wait on for a few seconds.
    static bool RunWithTimeout(ThreadStart action, int ms)
    {
        var t = new Thread(action);
        t.IsBackground = true;
        t.Start();
        return t.Join(ms);
    }

    // -1 means "could not read it", then we do not touch brightness at all.
    static int GetBrightness()
    {
        int result = -1;
        RunWithTimeout(delegate
        {
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness"))
                    foreach (ManagementObject o in s.Get())
                    {
                        result = Convert.ToInt32(o["CurrentBrightness"]);
                        break;
                    }
            }
            catch (Exception e) { Log("brightness read failed: " + e.Message); }
        }, 5000);
        return result;
    }

    static void SetBrightness(int value)
    {
        RunWithTimeout(delegate
        {
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                    foreach (ManagementObject o in s.Get())
                    {
                        o.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)value });
                        break;
                    }
            }
            catch (Exception e) { Log("brightness set failed: " + e.Message); }
        }, 5000);
    }

    // WMI reports success before the panel has actually taken the new value, so the
    // restore is verified by reading it back - a screen left at brightness 0 looks broken.
    static void RestoreBrightness(int value)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            SetBrightness(value);
            Thread.Sleep(600);
            int now = GetBrightness();
            if (now < 0 || now == value) { Log("brightness restored to " + value); return; }
            Log("brightness still " + now + " after attempt " + attempt);
        }
        Log("WARNING: could not restore brightness to " + value);
    }

    // Passive hook: it only listens for the exit combos, everything passes through.
    static IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            uint m = (uint)wParam;
            bool down = (m == WM_KEYDOWN || m == WM_SYSKEYDOWN);
            bool up = (m == WM_KEYUP || m == WM_SYSKEYUP);

            if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) { if (down) ctrlDown = true; if (up) ctrlDown = false; }
            if (vk == 0x12 || vk == 0xA4 || vk == 0xA5) { if (down) altDown = true; if (up) altDown = false; }

            if (vk == 0x44 && ctrlDown && altDown)
            {
                if (down) { Log("stop: Ctrl+Alt+D"); stopEvent.Set(); }
                return (IntPtr)1;   // only this one chord is swallowed, so no stray "D" is typed
            }

            // Panic exit: Esc three times within 2 seconds.
            if (down && vk == 0x1B)
            {
                int now = Environment.TickCount;
                if (escCount == 0 || now - escFirstTick > 2000) { escCount = 1; escFirstTick = now; }
                else escCount++;
                if (escCount >= 3) { Log("stop: Esc x3"); stopEvent.Set(); }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static Form MakeBlackWindow()
    {
        var f = new Form();
        f.FormBorderStyle = FormBorderStyle.None;
        f.BackColor = Color.Black;
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.StartPosition = FormStartPosition.Manual;
        f.Bounds = SystemInformation.VirtualScreen;   // covers every monitor
        f.Show();
        Cursor.Hide();
        return f;
    }

    // Deliberately no [STAThread], even though this hosts a WinForms window. The process
    // also talks to WMI and waits on a named stop event next to a message pump; while it
    // ran as STA that wait kept missing the event and the program only exited on its
    // timeout - the screen stayed dark for a minute and a half. MTA plus the explicit
    // stopEvent.WaitOne(0) check in the loop is what made the exit reliable. Both changes
    // landed together, so STA is not proven guilty - just do not switch back untested.
    static void Main(string[] args)
    {
        int delay = 700, timeoutSec = 10 * 3600;
        bool hard = false;
        foreach (string a in args)
        {
            // int.TryParse zeroes its out-param on failure, so parse into a temporary:
            // a typo'd "/timeout:abc" must not become a two-second timeout, and a typo'd
            // "hard" (no slash) must not silently become "no delay".
            int parsed;
            if (a.Equals("/hard", StringComparison.OrdinalIgnoreCase)) hard = true;
            else if (a.Equals("/soft", StringComparison.OrdinalIgnoreCase)) hard = false;
            else if (a.StartsWith("/timeout:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(a.Substring(9), out parsed) && parsed > 0) timeoutSec = parsed;
                else Log("ignoring bad timeout argument: " + a);
            }
            else if (int.TryParse(a, out parsed) && parsed >= 0) delay = parsed;
            else Log("ignoring unknown argument: " + a);
        }

        bool created;
        var mutex = new Mutex(false, MUTEX_NAME, out created);
        bool held = false;
        try { held = mutex.WaitOne(0, false); } catch (AbandonedMutexException) { held = true; }

        stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, EVENT_NAME);

        if (!held)
        {
            Log("signal stop (second instance)");
            stopEvent.Set();
            return;
        }

        stopEvent.Reset();
        Log("start " + (hard ? "/hard" : "/soft") + " (delay=" + delay + "ms, timeout=" + timeoutSec + "s)");
        Thread.Sleep(delay);   // let the hotkey keys be released, otherwise key-up wakes the screen

        int savedBrightness = GetBrightness();
        IntPtr hMod = GetModuleHandle(null);
        kbProc = KeyboardHook;
        IntPtr kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, hMod, 0);
        Log("hook=" + kbHook + " brightness was " + savedBrightness);

        // Soft mode also pins the display "in use" so Windows never blanks it by itself -
        // a blanked panel is what drags this machine into S0 standby and locks the session.
        SetThreadExecutionState(hard
            ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED)
            : (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED));

        Form black = null;
        int deadline = Environment.TickCount + timeoutSec * 1000;
        string reason = "unknown";
        try
        {
            if (savedBrightness >= 0) SetBrightness(0);
            if (!hard) black = MakeBlackWindow();

            IntPtr[] handles = { stopEvent.SafeWaitHandle.DangerousGetHandle() };
            if (hard) MonitorPower(MONITOR_OFF);

            while (true)
            {
                uint r = MsgWaitForMultipleObjects(1, handles, false,
                    (uint)(hard ? HARD_REFRESH_MS : SOFT_REFRESH_MS), QS_ALLINPUT);
                // Checked explicitly too: with COM/WMI in the process the wait above cannot
                // be trusted to report the event on its own.
                if (r == WAIT_OBJECT_0 || stopEvent.WaitOne(0, false)) { reason = "stop signal"; break; }
                if (r == WAIT_TIMEOUT)
                {
                    if (hard) MonitorPower(MONITOR_OFF);
                    else if (black != null) { black.TopMost = true; black.BringToFront(); }
                }
                else
                {
                    // Pump the queue - low-level hooks are not called without it.
                    MSG msg;
                    bool quit = false;
                    while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        if (msg.message == WM_QUIT) { quit = true; break; }
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                    if (quit) { reason = "WM_QUIT"; break; }
                }
                if (Environment.TickCount - deadline >= 0) { reason = "timeout"; break; }
            }
        }
        finally
        {
            if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook);
            SetThreadExecutionState(ES_CONTINUOUS);

            if (black != null)
            {
                Cursor.Show();
                try { black.Close(); black.Dispose(); } catch { }
            }
            if (hard)
            {
                Thread.Sleep(150);
                MonitorPower(MONITOR_ON);
                mouse_event(MOUSEEVENTF_MOVE, 0, 1, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_MOVE, 0, -1, 0, UIntPtr.Zero);
            }
            Log("exit (" + reason + ")");
            if (savedBrightness >= 0) RestoreBrightness(savedBrightness);
            try { mutex.ReleaseMutex(); } catch { }
        }
    }
}
