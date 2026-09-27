using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BetterSTT.UI;

/// <summary>
/// The click-through "Listening…" pill near the bottom of the screen. It never takes focus,
/// so text still lands in the app you were typing in.
/// </summary>
public partial class OverlayWindow : Window
{
    sealed record Palette(
        Color Background, Color Border, Color Text, Color Subtle, Color Recording,
        Color Accent, Color Track, Color Success, Color OnSuccess, Color KeyBackground, Color KeyBorder, double ShadowOpacity);

    static readonly Palette Light = new(
        Rgb(0xFB, 0xFB, 0xFB), Rgb(0xE0, 0xE0, 0xE0), Rgb(0x1B, 0x1B, 0x1B), Rgb(0x5C, 0x5C, 0x5C), Rgb(0xC4, 0x2B, 0x1C),
        Rgb(0x00, 0x5F, 0xB8), Rgb(0xE0, 0xE0, 0xE0), Rgb(0x0F, 0x7B, 0x0F), Colors.White, Colors.White, Rgb(0xCC, 0xCC, 0xCC), 0.18);

    static readonly Palette Dark = new(
        Rgb(0x2C, 0x2C, 0x2C), Rgb(0x3D, 0x3D, 0x3D), Colors.White, Rgb(0xC5, 0xC5, 0xC5), Rgb(0xFF, 0x99, 0xA4),
        Rgb(0x60, 0xCD, 0xFF), Rgb(0x44, 0x44, 0x44), Rgb(0x6C, 0xCB, 0x5F), Rgb(0x0B, 0x2A, 0x07), Rgb(0x37, 0x37, 0x37), Rgb(0x50, 0x50, 0x50), 0.45);

    readonly DictationController _c;
    readonly Rectangle[] _bars;
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DoubleAnimation _spin = new(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
    Palette _palette = Light;

    public OverlayWindow(DictationController controller)
    {
        InitializeComponent();
        _c = controller;
        _bars = new Rectangle[14];
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle { Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            Wave.Children.Add(_bars[i]);
        }

        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
        _clock.Tick += (_, _) => DetailText.Text = _c.Elapsed.ToString(@"m\:ss");

        _c.StateChanged += OnStateChanged;
        _c.LevelChanged += OnLevel;
        _c.DictationFinished += OnFinished;
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    /// <summary>For screenshots: shows a given state without a live dictation.</summary>
    public void Preview(string state, bool dark)
    {
        _palette = dark ? Dark : Light;
        switch (state)
        {
            case "listening":
                ShowListening();
                var rnd = new Random(3);
                foreach (var bar in _bars) bar.Height = 4 + rnd.NextDouble() * 18;
                DetailText.Text = "0:07";
                break;
            case "busy": ShowBusy(); break;
            default: ShowResult("Pasted", "4 filler words removed", success: true); break;
        }
        _clock.Stop();
        _hideTimer.Stop();
    }

    void OnStateChanged()
    {
        if (!_c.Settings.ShowOverlay)
        {
            Hide();
            return;
        }
        _palette = IsDarkTheme() ? Dark : Light;
        switch (_c.State)
        {
            case DictationState.Recording: ShowListening(); break;
            case DictationState.Transcribing: ShowBusy(); break;
        }
    }

    void OnFinished(DictationOutcome outcome)
    {
        if (!_c.Settings.ShowOverlay) return;
        var showFor = TimeSpan.FromMilliseconds(1600);
        switch (outcome.Kind)
        {
            case OutcomeKind.Pasted:
                int n = outcome.Item?.WordsRemoved ?? 0;
                ShowResult("Pasted", n switch { 0 => "nothing to clean", 1 => "1 filler word removed", _ => $"{n} filler words removed" }, true);
                break;
            case OutcomeKind.PastedLast:
                ShowResult("Pasted last dictation", "", true);
                break;
            case OutcomeKind.Copied:
                ShowResult("Copied to clipboard", "paste it with Ctrl+V", true);
                break;
            case OutcomeKind.Blocked:
                // The user has to act, so this one stays up longer.
                ShowResult("Copied to clipboard", "this window blocks typing, press Ctrl+V", false);
                showFor = TimeSpan.FromSeconds(4);
                break;
            case OutcomeKind.Recovered:
                ShowResult("Recovered", "copied to clipboard", true);
                break;
            case OutcomeKind.Cancelled:
                ShowResult("Cancelled", "", false);
                showFor = TimeSpan.FromMilliseconds(900);
                break;
            case OutcomeKind.NoSpeech:
                ShowResult("No speech detected", "", false);
                break;
            case OutcomeKind.Empty:
                ShowResult("Nothing to type", "", false);
                break;
            default:
                ShowResult("Dictation failed", "recording saved, retry from Home", false);
                showFor = TimeSpan.FromSeconds(4);
                break;
        }
        _hideTimer.Stop();
        _hideTimer.Interval = showFor;
        _hideTimer.Start();
    }

    void ShowListening()
    {
        _hideTimer.Stop();
        ApplyPalette();
        SetIndicator(recording: true);
        foreach (var bar in _bars) bar.Height = 4;
        Wave.Visibility = Visibility.Visible;
        TitleText.Text = "Listening";
        DetailText.Text = "0:00";
        DetailText.Visibility = Visibility.Visible;
        Keys.Children.Clear();
        foreach (var part in _c.Settings.Hotkey.Parts()) Keys.Children.Add(Keycap(part));
        StopText.Text = _c.Settings.Activation == ActivationMode.Hold ? "release to stop" : "to stop";
        StopHint.Visibility = Visibility.Visible;
        _clock.Start();
        ShowAtBottom();
    }

    void ShowBusy()
    {
        _hideTimer.Stop();
        _clock.Stop();
        ApplyPalette();
        SetIndicator(spinner: true);
        Wave.Visibility = Visibility.Collapsed;
        TitleText.Text = "Transcribing…";
        DetailText.Visibility = Visibility.Collapsed;
        StopHint.Visibility = Visibility.Collapsed;
        ShowAtBottom();
    }

    void ShowResult(string title, string detail, bool success)
    {
        _clock.Stop();
        ApplyPalette();
        SetIndicator(check: success);
        Wave.Visibility = Visibility.Collapsed;
        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StopHint.Visibility = Visibility.Collapsed;
        ShowAtBottom();
    }

    void SetIndicator(bool recording = false, bool spinner = false, bool check = false)
    {
        RecDot.Visibility = RecHalo.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        Spinner.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;
        Check.Visibility = check ? Visibility.Visible : Visibility.Collapsed;
        if (spinner) SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, _spin);
        else SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        // Neutral results (no speech, failed) show just the grey dot.
        if (!recording && !spinner && !check)
        {
            RecDot.Visibility = Visibility.Visible;
            RecDot.Fill = new SolidColorBrush(_palette.Subtle);
        }
    }

    void ApplyPalette()
    {
        var p = _palette;
        Pill.Background = new SolidColorBrush(p.Background);
        Pill.BorderBrush = new SolidColorBrush(p.Border);
        Shadow.Opacity = p.ShadowOpacity;
        TitleText.Foreground = new SolidColorBrush(p.Text);
        DetailText.Foreground = StopText.Foreground = EscText.Foreground = new SolidColorBrush(p.Subtle);
        Separator.Fill = new SolidColorBrush(p.Border);
        RecDot.Fill = RecHalo.Fill = new SolidColorBrush(p.Recording);
        foreach (var bar in _bars) bar.Fill = new SolidColorBrush(p.Recording);
        SpinnerTrack.Stroke = new SolidColorBrush(p.Track);
        SpinnerArc.Stroke = new SolidColorBrush(p.Accent);
        CheckCircle.Fill = new SolidColorBrush(p.Success);
        CheckMark.Stroke = new SolidColorBrush(p.OnSuccess);
    }

    Border Keycap(string text) => new()
    {
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1, 1, 1, 2),
        Padding = new Thickness(6, 1, 6, 1),
        Margin = new Thickness(0, 0, 4, 0),
        Background = new SolidColorBrush(_palette.KeyBackground),
        BorderBrush = new SolidColorBrush(_palette.KeyBorder),
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(_palette.Text) },
    };

    void OnLevel(float level)
    {
        if (_c.State != DictationState.Recording || !IsVisible) return;
        for (int i = 0; i < _bars.Length - 1; i++) _bars[i].Height = _bars[i + 1].Height;
        _bars[^1].Height = 4 + Math.Min(1.0, level * 4) * 18;
    }

    void ShowAtBottom()
    {
        if (!IsVisible) Show();
        UpdateLayout();
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = area.Left / dpi.DpiScaleX + (area.Width / dpi.DpiScaleX - ActualWidth) / 2;
        Top = area.Bottom / dpi.DpiScaleY - ActualHeight - 24;
    }

    static bool IsDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
