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
    bool _building;
    HotkeyTarget? _capturing;
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

        foreach (var (_, label) in TailOptions) TailBox.Items.Add(label);
        TailBox.SelectionChanged += (_, _) =>
        {
            if (_building || TailBox.SelectedIndex < 0) return;
            _c.Update(s => s.TailCaptureMs = TailOptions[TailBox.SelectedIndex].Ms);
        };
        PasteLastSwitch.Checked += (_, _) => { if (!_building) _c.Update(s => s.PasteLastEnabled = true); };
        PasteLastSwitch.Unchecked += (_, _) => { if (!_building) _c.Update(s => s.PasteLastEnabled = false); };

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

        // Nearest listed option to the saved value.
        int tail = _c.Settings.TailCaptureMs;
        TailBox.SelectedIndex = Array.IndexOf(TailOptions, TailOptions.MinBy(o => Math.Abs(o.Ms - tail)));
        PasteLastSwitch.IsChecked = _c.Settings.PasteLastEnabled;
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

    // ---- shortcuts ----

    void RefreshShortcut()
    {
        if (_capturing != null) return;
        bool hold = _c.Settings.Activation == ActivationMode.Hold;

        ShortcutKeys.Content = Theme.Keycaps(_c.Settings.Hotkey, 13);
        ChangeButton.Content = "Change";
        ShortcutHint.Text = !_c.HotkeyRegistered
            ? "Another app is using this shortcut. Choose a different one."
            : hold
                ? "Hold it down while you speak, let go to paste. Esc cancels."
                : "Press once to start listening, press again to stop and paste. Esc cancels.";
        ShortcutHint.SetResourceReference(TextBlock.ForegroundProperty, _c.HotkeyRegistered ? Theme.TextSecondary : Theme.Critical);

        ToggleModeButton.Appearance = hold ? ControlAppearance.Transparent : ControlAppearance.Primary;
        HoldModeButton.Appearance = hold ? ControlAppearance.Primary : ControlAppearance.Transparent;
        ActivationHint.Text = hold
            ? "Hold the shortcut while you talk, like a walkie-talkie."
            : "Press to start listening, press again to stop.";

        bool pasteLastOn = _c.Settings.PasteLastEnabled;
        PasteLastKeys.Content = Theme.Keycaps(_c.Settings.PasteLastHotkey, 13);
        PasteLastKeys.Opacity = PasteLastChangeButton.Opacity = pasteLastOn ? 1 : 0.4;
        PasteLastChangeButton.IsEnabled = pasteLastOn;
        PasteLastChangeButton.Content = "Change";
        bool pasteLastBroken = pasteLastOn && !_c.PasteLastRegistered;
        PasteLastHint.Text = pasteLastBroken
            ? "Another app is using this shortcut. Choose a different one."
            : "Types your most recent dictation again, e.g. if it landed in the wrong window.";
        PasteLastHint.SetResourceReference(TextBlock.ForegroundProperty, pasteLastBroken ? Theme.Critical : Theme.TextSecondary);
    }

    void OnToggleMode(object sender, RoutedEventArgs e) => _c.Update(s => s.Activation = ActivationMode.Toggle);

    void OnHoldMode(object sender, RoutedEventArgs e) => _c.Update(s => s.Activation = ActivationMode.Hold);

    void OnChangeShortcut(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.Dictate);

    void OnChangePasteLast(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.PasteLast);

    (ContentControl Keys, TextBlock Hint, Wpf.Ui.Controls.Button Button) CaptureUi(HotkeyTarget target) =>
        target == HotkeyTarget.Dictate
            ? (ShortcutKeys, ShortcutHint, ChangeButton)
            : (PasteLastKeys, PasteLastHint, PasteLastChangeButton);

    void BeginCapture(HotkeyTarget target)
    {
        if (_capturing != null)
        {
            // Clicking "Cancel", or Change on the other shortcut, ends the current capture.
            bool same = _capturing == target;
            EndCapture();
            RefreshShortcut();
            if (same) return;
        }
        _capturing = target;
        _c.SuspendHotkey(); // shortcuts pause only while the new combination is being pressed
        var (keys, hint, button) = CaptureUi(target);
        button.Content = "Cancel";
        keys.Content = Theme.Text("Press the new shortcut…", 14, Theme.TextSecondary);
        hint.Text = "Use at least one of Ctrl, Alt, Shift or Win, plus a key. Esc cancels.";
        hint.SetResourceReference(TextBlock.ForegroundProperty, Theme.TextSecondary);
        if (_window != null) _window.PreviewKeyDown += OnCaptureKey;
    }

    void OnCaptureKey(object sender, KeyEventArgs e)
    {
        if (_capturing is not { } target) return;
        e.Handled = true;
        var (_, hint, _) = CaptureUi(target);
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
            hint.Text = "Add Ctrl, Alt, Shift or Win so normal typing isn't captured.";
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

        if (_c.TrySetHotkey(target, binding) is { } problem)
        {
            hint.Text = problem;
            hint.SetResourceReference(TextBlock.ForegroundProperty, Theme.Critical);
            return;
        }
        EndCapture();
        RefreshShortcut();
    }

    void EndCapture()
    {
        if (_capturing == null) return;
        _capturing = null;
        if (_window != null) _window.PreviewKeyDown -= OnCaptureKey;
        _c.ResumeHotkey();
    }

    // ---- tail capture ----

    static readonly (int Ms, string Label)[] TailOptions =
    [
        (0, "Off"), (200, "0.2 seconds"), (300, "0.3 seconds (recommended)"), (500, "0.5 seconds"), (800, "0.8 seconds"),
    ];

    void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_capturing == null) return;
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
