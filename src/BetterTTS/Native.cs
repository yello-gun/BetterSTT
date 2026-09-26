using System.Runtime.InteropServices;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace BetterTTS;

/// <summary>Registers a system-wide hotkey via a hidden message-only window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const int Id = 1;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    readonly HwndSource _window;
    bool _registered;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        _window = new HwndSource(new HwndSourceParameters("BetterTTSHotkey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    public bool Register(HotkeyBinding b)
    {
        Unregister();
        uint mods = MOD_NOREPEAT;
        if (b.Ctrl) mods |= MOD_CONTROL;
        if (b.Alt) mods |= MOD_ALT;
        if (b.Shift) mods |= MOD_SHIFT;
        if (b.Win) mods |= MOD_WIN;
        _registered = RegisterHotKey(_window.Handle, Id, mods, (uint)b.Key);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_window.Handle, Id);
        _registered = false;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }

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

    /// <summary>True when one of BetterTTS's own windows has focus, where pasting would be pointless.</summary>
    public static bool IsOwnWindowFocused()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

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
        return key?.GetValue("BetterTTS") != null;
    }

    public static void Apply(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        // --background: start quietly in the tray without opening the window.
        if (enabled) key.SetValue("BetterTTS", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("BetterTTS", throwOnMissingValue: false);
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
