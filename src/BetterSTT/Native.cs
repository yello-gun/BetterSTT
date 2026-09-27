using System.Runtime.InteropServices;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace BetterSTT;

/// <summary>Registers system-wide hotkeys via a hidden message-only window, each under its own id.</summary>
public sealed class GlobalHotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    readonly HwndSource _window;
    readonly HashSet<int> _registered = new();

    /// <summary>Raised with the id of the hotkey that was pressed.</summary>
    public event Action<int>? Pressed;

    public GlobalHotkey()
    {
        _window = new HwndSource(new HwndSourceParameters("BetterSTTHotkey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    public bool Register(int id, HotkeyBinding b)
    {
        Unregister(id);
        uint mods = MOD_NOREPEAT;
        if (b.Ctrl) mods |= MOD_CONTROL;
        if (b.Alt) mods |= MOD_ALT;
        if (b.Shift) mods |= MOD_SHIFT;
        if (b.Win) mods |= MOD_WIN;
        bool ok = RegisterHotKey(_window.Handle, id, mods, (uint)b.Key);
        if (ok) _registered.Add(id);
        return ok;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id)) UnregisterHotKey(_window.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered.ToList()) Unregister(id);
    }

    /// <summary>Whether a combination is free, without keeping it registered.</summary>
    public bool IsAvailable(HotkeyBinding b)
    {
        const int probeId = 0x7FFF;
        bool ok = Register(probeId, b);
        Unregister(probeId);
        return ok;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            Pressed?.Invoke(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }

    /// <summary>True while the key is physically held down (used for hold-to-talk).</summary>
    public static bool IsKeyDown(WinForms.Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>Delivers text to whichever app currently has keyboard focus.</summary>
public static class TextInjector
{
    public static async Task InjectAsync(string text, AppSettings s)
    {
        await WaitForModifiersReleasedAsync();
        if (s.OutputMethod == OutputMethod.Type)
        {
            TypeUnicode(text);
            return;
        }

        WinForms.IDataObject? backup = s.RestoreClipboard ? SnapshotClipboard() : null;
        SetClipboard(text);
        SendKeyCombo(VK_CONTROL, VK_V);

        if (backup != null)
        {
            await Task.Delay(300); // give the target app time to read the clipboard
            try { WinForms.Clipboard.SetDataObject(backup, true, 10, 50); }
            catch (Exception ex) { Log.Write($"Clipboard restore failed: {ex.Message}"); }
        }
    }

    /// <summary>Copies text, keeping it out of Windows clipboard history (Win+V) and cloud clipboard.</summary>
    public static void SetClipboard(string text)
    {
        var data = new WinForms.DataObject();
        data.SetText(text, WinForms.TextDataFormat.UnicodeText);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
        WinForms.Clipboard.SetDataObject(data, true, 10, 50);
    }

    /// <summary>True when one of BetterSTT's own windows has focus, where pasting would be pointless.</summary>
    public static bool IsOwnWindowFocused()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// True when the focused window belongs to a process running at a higher integrity level than
    /// BetterSTT, typically an app started as administrator. Windows silently drops simulated
    /// keystrokes sent to those windows (UIPI), so pasting there would lose the text.
    /// </summary>
    public static bool IsForegroundBlocked()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

            int own = IntegrityOf(GetCurrentProcess()) ?? MediumIntegrity;
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return own < HighIntegrity; // can't even inspect it: protected or elevated
            try
            {
                // An unreadable token also means it's elevated relative to us.
                int target = IntegrityOf(process) ?? int.MaxValue;
                return target > own;
            }
            finally
            {
                CloseHandle(process);
            }
        }
        catch
        {
            return false;
        }
    }

    const int MediumIntegrity = 0x2000, HighIntegrity = 0x3000;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x8;
    const int TokenIntegrityLevel = 25;

    static int? IntegrityOf(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _)) return null;
                IntPtr sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    static WinForms.IDataObject? SnapshotClipboard()
    {
        try
        {
            var source = WinForms.Clipboard.GetDataObject();
            if (source == null) return null;
            var copy = new WinForms.DataObject();
            int kept = 0;
            foreach (var format in source.GetFormats(false))
            {
                try
                {
                    var value = source.GetData(format, false);
                    if (value == null) continue;
                    copy.SetData(format, false, value);
                    kept++;
                }
                catch
                {
                    // Some formats (delay-rendered, OLE-only) cannot be copied; skip them.
                }
            }
            return kept > 0 ? copy : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The stop hotkey's modifiers may still be held; pasting then would send Ctrl+Alt+V etc.</summary>
    static async Task WaitForModifiersReleasedAsync()
    {
        int[] keys = [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN];
        for (int waited = 0; waited < 2000; waited += 20)
        {
            if (keys.All(k => (GetAsyncKeyState(k) & 0x8000) == 0)) return;
            await Task.Delay(20);
        }
    }

    static void SendKeyCombo(ushort modifier, ushort key)
    {
        var inputs = new[]
        {
            Key(modifier, 0, 0), Key(key, 0, 0),
            Key(key, 0, KEYEVENTF_KEYUP), Key(modifier, 0, KEYEVENTF_KEYUP),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static void TypeUnicode(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("BetterSTT") != null;
    }

    public static void Apply(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        // --background: start quietly in the tray without opening the window.
        if (enabled) key.SetValue("BetterSTT", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("BetterSTT", throwOnMissingValue: false);
    }
}

public static class SystemInfo
{
    /// <summary>The name of the dedicated GPU if there is one (NVIDIA/AMD/Intel Arc), else the first display adapter.</summary>
    public static string? GpuName { get; } = FindGpu();

    static string? FindGpu()
    {
        try
        {
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey == null) return null;
            var names = classKey.GetSubKeyNames()
                .Where(n => n.All(char.IsDigit))
                .Select(n => { using var k = classKey.OpenSubKey(n); return k?.GetValue("DriverDesc") as string; })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Cast<string>()
                .ToList();
            return names.FirstOrDefault(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                                             || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                                             || n.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                   ?? names.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
