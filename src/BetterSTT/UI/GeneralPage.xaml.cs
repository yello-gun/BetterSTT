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
        foreach (var (_, label) in PositionOptions) PositionBox.Items.Add(label);
        PositionBox.SelectionChanged += (_, _) =>
        {
            if (_building || PositionBox.SelectedIndex < 0) return;
            _c.Update(s => s.OverlayPosition = PositionOptions[PositionBox.SelectedIndex].Position);
        };
        ScreenBox.Items.Add("Screen with the mouse");
        ScreenBox.Items.Add("Main screen");
        ScreenBox.SelectionChanged += (_, _) =>
        {
            if (_building || ScreenBox.SelectedIndex < 0) return;
            _c.Update(s => s.OverlayOnPrimaryScreen = ScreenBox.SelectedIndex == 1);
        };
        OverlaySwitch.Checked += (_, _) => { OverlayDetails.Visibility = Visibility.Visible; if (!_building) _c.Update(s => s.ShowOverlay = true); };
        OverlaySwitch.Unchecked += (_, _) => { OverlayDetails.Visibility = Visibility.Collapsed; if (!_building) _c.Update(s => s.ShowOverlay = false); };
        LivePreviewSwitch.Checked += (_, _) => { if (!_building) _c.Update(s => s.LivePreview = true); };
        LivePreviewSwitch.Unchecked += (_, _) => { if (!_building) _c.Update(s => s.LivePreview = false); };
        PasteLastSwitch.Checked += (_, _) => { if (!_building) _c.Update(s => s.PasteLastEnabled = true); };
        PasteLastSwitch.Unchecked += (_, _) => { if (!_building) _c.Update(s => s.PasteLastEnabled = false); };
        AddWordSwitch.Checked += (_, _) => { if (!_building) _c.Update(s => s.AddWordEnabled = true); };
        AddWordSwitch.Unchecked += (_, _) => { if (!_building) _c.Update(s => s.AddWordEnabled = false); };
        foreach (var (_, label) in SizeOptions) SizeBox.Items.Add(label);
        SizeBox.SelectionChanged += (_, _) =>
        {
            if (_building || SizeBox.SelectedIndex < 0) return;
            _c.Update(s => s.OverlaySize = SizeOptions[SizeBox.SelectedIndex].Size);
            App.Overlay?.ShowPreview();
        };
        foreach (int o in OpacityOptions) OpacityBox.Items.Add($"{o}%");
        OpacityBox.SelectionChanged += (_, _) =>
        {
            if (_building || OpacityBox.SelectedIndex < 0) return;
            _c.Update(s => s.OverlayOpacity = OpacityOptions[OpacityBox.SelectedIndex]);
            App.Overlay?.ShowPreview();
        };
        UpdateSwitch.Checked += (_, _) => { if (!_building) _c.Update(s => s.CheckForUpdates = true); RefreshUpdates(); };
        UpdateSwitch.Unchecked += (_, _) => { if (!_building) _c.Update(s => s.CheckForUpdates = false); RefreshUpdates(); };

        var version = typeof(GeneralPage).Assembly.GetName().Version;
        AboutText.Text = $"BetterSTT {version?.ToString(3)} · Settings and models are stored in %LOCALAPPDATA%\\BetterSTT";

        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window != null) _window.Deactivated += OnWindowDeactivated;
            _c.SettingsChanged += RefreshShortcut;
            _c.UpdateChanged += RefreshUpdates;
            Build();
            _monitor.Start(_c.Settings.MicrophoneDevice);
        };
        Unloaded += (_, _) =>
        {
            EndCapture();
            if (_window != null) _window.Deactivated -= OnWindowDeactivated;
            _c.SettingsChanged -= RefreshShortcut;
            _c.UpdateChanged -= RefreshUpdates;
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
        AddWordSwitch.IsChecked = _c.Settings.AddWordEnabled;
        SizeBox.SelectedIndex = Math.Max(0, Array.FindIndex(SizeOptions, o => o.Size == _c.Settings.OverlaySize));
        OpacityBox.SelectedIndex = Math.Max(0, Array.IndexOf(OpacityOptions, OpacityOptions.MinBy(o => Math.Abs(o - _c.Settings.OverlayOpacity))));
        PositionBox.SelectedIndex = Math.Max(0, Array.FindIndex(PositionOptions, o => o.Position == _c.Settings.OverlayPosition));
        ScreenBox.SelectedIndex = _c.Settings.OverlayOnPrimaryScreen ? 1 : 0;
        OverlaySwitch.IsChecked = _c.Settings.ShowOverlay;
        OverlayDetails.Visibility = _c.Settings.ShowOverlay ? Visibility.Visible : Visibility.Collapsed;
        LivePreviewSwitch.IsChecked = _c.Settings.LivePreview;
        UpdateSwitch.IsChecked = _c.Settings.CheckForUpdates;
        _building = false;
        RefreshUpdates();

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
        var mode = _c.Settings.Activation;
        bool hold = mode == ActivationMode.Hold;

        ShortcutKeys.Content = Theme.Keycaps(_c.Settings.Hotkey, 13);
        ChangeButton.Content = "Change";
        ShortcutHint.Text = !_c.HotkeyRegistered
            ? "Another app is using this shortcut. Choose a different one."
            : mode switch
            {
                ActivationMode.Hold => "Hold it down while you speak, let go to paste. Esc cancels.",
                ActivationMode.HandsFree => "Press once to start hands-free listening, press again to type everything you said. Esc cancels.",
                _ => "Press once to start listening, press again to stop and paste. Esc cancels.",
            };
        ShortcutHint.SetResourceReference(TextBlock.ForegroundProperty, _c.HotkeyRegistered ? Theme.TextSecondary : Theme.Critical);

        ToggleModeButton.Appearance = mode == ActivationMode.Toggle ? ControlAppearance.Primary : ControlAppearance.Transparent;
        HoldModeButton.Appearance = hold ? ControlAppearance.Primary : ControlAppearance.Transparent;
        HandsFreeModeButton.Appearance = mode == ActivationMode.HandsFree ? ControlAppearance.Primary : ControlAppearance.Transparent;
        ActivationHint.Text = mode switch
        {
            ActivationMode.Hold => "Hold the shortcut while you talk, like a walkie-talkie.",
            ActivationMode.HandsFree => "Keeps listening with no time limit until you press the shortcut again, then types everything at once. It transcribes quietly at each pause, so long sessions finish quickly.",
            _ => "Press to start listening, press again to stop.",
        };

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

        bool addWordOn = _c.Settings.AddWordEnabled;
        AddWordKeys.Content = Theme.Keycaps(_c.Settings.AddWordHotkey, 13);
        AddWordKeys.Opacity = AddWordChangeButton.Opacity = addWordOn ? 1 : 0.4;
        AddWordChangeButton.IsEnabled = addWordOn;
        AddWordChangeButton.Content = "Change";
        bool addWordBroken = addWordOn && !_c.AddWordRegistered;
        AddWordHint.Text = addWordBroken
            ? "Another app is using this shortcut. Choose a different one."
            : "Select a word in any app and press this to add it to your dictionary.";
        AddWordHint.SetResourceReference(TextBlock.ForegroundProperty, addWordBroken ? Theme.Critical : Theme.TextSecondary);
    }

    void OnToggleMode(object sender, RoutedEventArgs e) => _c.Update(s => s.Activation = ActivationMode.Toggle);

    void OnHoldMode(object sender, RoutedEventArgs e) => _c.Update(s => s.Activation = ActivationMode.Hold);

    void OnHandsFreeMode(object sender, RoutedEventArgs e) => _c.Update(s => s.Activation = ActivationMode.HandsFree);

    void OnChangeShortcut(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.Dictate);

    void OnChangePasteLast(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.PasteLast);

    void OnChangeAddWord(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.AddWord);

    (ContentControl Keys, TextBlock Hint, Wpf.Ui.Controls.Button Button) CaptureUi(HotkeyTarget target) =>
        target switch
        {
            HotkeyTarget.Dictate => (ShortcutKeys, ShortcutHint, ChangeButton),
            HotkeyTarget.PasteLast => (PasteLastKeys, PasteLastHint, PasteLastChangeButton),
            _ => (AddWordKeys, AddWordHint, AddWordChangeButton),
        };

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
        hint.Text = "Use at least one of Ctrl, Alt, Shift or Win, plus a key, or click a mouse side button. Esc cancels.";
        hint.SetResourceReference(TextBlock.ForegroundProperty, Theme.TextSecondary);
        if (_window != null)
        {
            _window.PreviewKeyDown += OnCaptureKey;
            _window.PreviewMouseDown += OnCaptureMouse;
        }
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

        SaveCapture(target, (WinFormsKeys)KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>The mouse's side buttons (and middle click, with a modifier) can be the shortcut too.</summary>
    void OnCaptureMouse(object sender, MouseButtonEventArgs e)
    {
        if (_capturing is not { } target) return;
        WinFormsKeys? button = e.ChangedButton switch
        {
            MouseButton.XButton1 => WinFormsKeys.XButton1,
            MouseButton.XButton2 => WinFormsKeys.XButton2,
            MouseButton.Middle => WinFormsKeys.MButton,
            _ => null,
        };
        if (button == null) return; // left and right clicks still work normally, e.g. on Cancel
        e.Handled = true;
        SaveCapture(target, button.Value);
    }

    void SaveCapture(HotkeyTarget target, WinFormsKeys key)
    {
        var (_, hint, _) = CaptureUi(target);
        var mods = Keyboard.Modifiers;
        var binding = new HotkeyBinding
        {
            Key = key,
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
        if (_window != null)
        {
            _window.PreviewKeyDown -= OnCaptureKey;
            _window.PreviewMouseDown -= OnCaptureMouse;
        }
        _c.ResumeHotkey();
    }

    // ---- indicator position ----

    static readonly (OverlayPosition Position, string Label)[] PositionOptions =
    [
        (OverlayPosition.BottomCenter, "Bottom center"), (OverlayPosition.BottomLeft, "Bottom left"),
        (OverlayPosition.BottomRight, "Bottom right"), (OverlayPosition.TopCenter, "Top center"),
        (OverlayPosition.TopLeft, "Top left"), (OverlayPosition.TopRight, "Top right"),
    ];

    void OnPreviewOverlay(object sender, RoutedEventArgs e) => App.Overlay?.ShowPreview();

    static readonly (OverlaySize Size, string Label)[] SizeOptions =
        [(OverlaySize.Small, "Small"), (OverlaySize.Normal, "Normal"), (OverlaySize.Large, "Large")];

    static readonly int[] OpacityOptions = [100, 85, 70, 55];

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

    // ---- updates and diagnostics ----

    void RefreshUpdates()
    {
        string version = Updater.Current.ToString(3);
        CheckButton.IsEnabled = !_c.CheckingForUpdates;
        if (_c.AvailableUpdate is { } update)
        {
            UpdateStatus.Text = $"BetterSTT {update.Version.ToString(3)} is downloaded. It installs the next time you open the app.";
            CheckButton.Content = "Install now";
            return;
        }
        CheckButton.Content = "Check now";
        string checkedAt = _c.Settings.LastUpdateCheck is { } last ? $" Last checked {Theme.TimeAgo(last).Replace("Just now", "just now")}." : "";
        UpdateStatus.Text = _c.CheckingForUpdates
            ? "Checking GitHub for a new version…"
            : _c.UpdateError is { } error
                ? $"The check didn't work: {error}"
                : _c.Settings.CheckForUpdates
                    ? $"You have {version}. BetterSTT checks GitHub once a day and downloads new versions in the background.{checkedAt}"
                    : $"You have {version}. Automatic checks are off; nothing is sent until you click Check now.{checkedAt}";
    }

    async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_c.AvailableUpdate != null)
        {
            if (!((App)Application.Current).InstallUpdate())
                UpdateStatus.Text = "The update couldn't start while a dictation is in progress. Try again in a moment.";
            return;
        }
        await _c.CheckForUpdatesAsync();
        if (_c.AvailableUpdate == null && _c.UpdateError == null)
            UpdateStatus.Text = $"You have the latest version ({Updater.Current.ToString(3)}).";
    }

    void OnSaveDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"BetterSTT-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "Zip file|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            Diagnostics.Save(dialog.FileName, _c);
            DiagnosticsHint.Text = $"Saved {Path.GetFileName(dialog.FileName)}, with {string.Join(", ", Diagnostics.Contents)}. Open it to check what's inside before sharing it.";
            Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
        }
        catch (Exception ex)
        {
            DiagnosticsHint.Text = $"Couldn't save it: {ex.Message}";
        }
    }

    void OnOpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Data);
        Process.Start("explorer.exe", AppPaths.Data);
    }
}
