using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace BetterSTT.UI;

public partial class GeneralPage : Page
{
    readonly DictationController _c = App.Controller;
    readonly LevelMonitor _monitor = new();
    bool _building, _capturing;
    Window? _window;
    float _level;

    sealed record Toggle(string Title, string Description, Func<AppSettings, bool> Get, Action<AppSettings, bool> Set);

    static readonly Toggle[] ToggleDefs =
    [
        new("Restore my clipboard", "Puts back whatever you had copied after pasting a dictation.",
            s => s.RestoreClipboard, (s, v) => s.RestoreClipboard = v),
        new("Add a space after each dictation", "So back-to-back dictations don't run together.",
            s => s.AddTrailingSpace, (s, v) => s.AddTrailingSpace = v),
        new("Start and stop sounds", "A short chime when listening starts and stops.",
            s => s.PlaySounds, (s, v) => s.PlaySounds = v),
        new("Listening indicator", "A small pill near the bottom of the screen while you talk.",
            s => s.ShowOverlay, (s, v) => s.ShowOverlay = v),
        new("Start with Windows", "Runs quietly in the system tray when you sign in.",
            s => s.StartWithWindows, (s, v) => s.StartWithWindows = v),
    ];

    public GeneralPage()
    {
        InitializeComponent();

        MicBox.SelectionChanged += (_, _) =>
        {
            if (_building || MicBox.SelectedIndex < 0) return;
            _c.Update(s => s.MicrophoneDevice = MicBox.SelectedIndex - 1);
            _monitor.Start(_c.Settings.MicrophoneDevice);
        };
        _monitor.LevelChanged += level => Dispatcher.BeginInvoke(() => ShowLevel(level));

        var version = typeof(GeneralPage).Assembly.GetName().Version;
        AboutText.Text = $"BetterSTT {version?.ToString(3)} · Settings and models are stored in %LOCALAPPDATA%\\BetterSTT";

        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window != null) _window.Deactivated += OnWindowDeactivated;
            _c.SettingsChanged += RefreshShortcut;
            Build();
            _monitor.Start(_c.Settings.MicrophoneDevice);
        };
        Unloaded += (_, _) =>
        {
            EndCapture();
            if (_window != null) _window.Deactivated -= OnWindowDeactivated;
            _c.SettingsChanged -= RefreshShortcut;
            _monitor.Stop();
        };
    }

    void Build()
    {
        _building = true;
        MicBox.Items.Clear();
        MicBox.Items.Add("Windows default microphone");
        try { foreach (var d in AudioRecorder.GetDevices()) MicBox.Items.Add(d); }
        catch { /* no audio subsystem */ }
        int index = _c.Settings.MicrophoneDevice + 1;
        MicBox.SelectedIndex = index >= 0 && index < MicBox.Items.Count ? index : 0;
        _building = false;

        RefreshShortcut();
        RefreshOutput();

        Toggles.Children.Clear();
        foreach (var t in ToggleDefs) Toggles.Children.Add(ToggleCard(t));
    }

    Border ToggleCard(Toggle t)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Theme.Text(t.Title));
        var desc = Theme.Text(t.Description, 12, Theme.TextSecondary);
        desc.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(desc);

        var toggle = new ToggleSwitch { IsChecked = t.Get(_c.Settings), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(toggle, t.Title);
        toggle.Checked += (_, _) => _c.Update(s => t.Set(s, true));
        toggle.Unchecked += (_, _) => _c.Update(s => t.Set(s, false));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(texts);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return new Border { Child = grid, Style = (Style)FindResource("Card") };
    }

    // ---- shortcut ----

    void RefreshShortcut()
    {
        if (_capturing) return;
        ShortcutKeys.Content = Theme.Keycaps(_c.Settings.Hotkey, 13);
        ChangeButton.Content = "Change";
        ShortcutHint.Text = _c.HotkeyRegistered
            ? "Press once to start listening, press again to stop and paste."
            : "Another app is using this shortcut. Choose a different one.";
        ShortcutHint.SetResourceReference(TextBlock.ForegroundProperty, _c.HotkeyRegistered ? Theme.TextSecondary : Theme.Critical);
    }

    void OnChangeShortcut(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            EndCapture();
            RefreshShortcut();
            return;
        }
        _capturing = true;
        _c.SuspendHotkey(); // only paused while recording the new combination
        ChangeButton.Content = "Cancel";
        ShortcutKeys.Content = Theme.Text("Press the new shortcut…", 14, Theme.TextSecondary);
        ShortcutHint.Text = "Use at least one of Ctrl, Alt, Shift or Win, plus a key. Esc cancels.";
        ShortcutHint.SetResourceReference(TextBlock.ForegroundProperty, Theme.TextSecondary);
        if (_window != null) _window.PreviewKeyDown += OnCaptureKey;
    }

    void OnCaptureKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }
        if (key == Key.Escape)
        {
            EndCapture();
            RefreshShortcut();
            return;
        }

        var mods = Keyboard.Modifiers;
        bool isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (mods == ModifierKeys.None && !isFunctionKey)
        {
            ShortcutHint.Text = "Add Ctrl, Alt, Shift or Win so normal typing isn't captured.";
            return;
        }

        var binding = new HotkeyBinding
        {
            Key = (WinFormsKeys)KeyInterop.VirtualKeyFromKey(key),
            Ctrl = mods.HasFlag(ModifierKeys.Control),
            Alt = mods.HasFlag(ModifierKeys.Alt),
            Shift = mods.HasFlag(ModifierKeys.Shift),
            Win = mods.HasFlag(ModifierKeys.Windows),
        };

        if (!_c.TrySetHotkey(binding))
        {
            ShortcutHint.Text = $"{binding} is already used by another app. Try another combination.";
            ShortcutHint.SetResourceReference(TextBlock.ForegroundProperty, Theme.Critical);
            return;
        }
        EndCapture();
        RefreshShortcut();
    }

    void EndCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        if (_window != null) _window.PreviewKeyDown -= OnCaptureKey;
        _c.ResumeHotkey();
    }

    void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!_capturing) return;
        EndCapture();
        RefreshShortcut();
    }

    // ---- microphone meter ----

    void ShowLevel(float peak)
    {
        _level = Math.Max(peak, _level * 0.8f); // fast attack, gentle fall
        double fraction = Math.Min(1.0, _level * 2.5);
        MeterFill.Width = MeterTrack.ActualWidth * fraction;
        MeterLabel.Text = fraction switch
        {
            < 0.05 => "Silent",
            < 0.25 => "Quiet",
            _ => "Good",
        };
    }

    // ---- output ----

    void RefreshOutput()
    {
        bool paste = _c.Settings.OutputMethod == OutputMethod.Paste;
        PasteButton.Appearance = paste ? ControlAppearance.Primary : ControlAppearance.Transparent;
        TypeButton.Appearance = paste ? ControlAppearance.Transparent : ControlAppearance.Primary;
        OutputHint.Text = paste
            ? "Fast and works almost everywhere."
            : "Types each character, for apps that block pasting.";
    }

    void OnPaste(object sender, RoutedEventArgs e)
    {
        _c.Update(s => s.OutputMethod = OutputMethod.Paste);
        RefreshOutput();
    }

    void OnType(object sender, RoutedEventArgs e)
    {
        _c.Update(s => s.OutputMethod = OutputMethod.Type);
        RefreshOutput();
    }

    void OnOpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Data);
        Process.Start("explorer.exe", AppPaths.Data);
    }
}
