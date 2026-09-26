using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BetterSTT.UI;

public partial class HomePage : Page
{
    readonly DictationController _c = App.Controller;
    readonly Rectangle[] _bars;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    int _expanded = 0;

    public HomePage()
    {
        InitializeComponent();
        _bars = Theme.AddWaveBars(Wave, 16, Theme.Critical);
        _clock.Tick += (_, _) => ElapsedText.Text = _c.Elapsed.ToString(@"m\:ss");

        Loaded += (_, _) =>
        {
            _c.StateChanged += RefreshStatus;
            _c.ModelChanged += RefreshStatus;
            _c.SettingsChanged += RefreshAll;
            _c.LevelChanged += OnLevel;
            _c.DictationFinished += OnFinished;
            RefreshAll();
        };
        Unloaded += (_, _) =>
        {
            _c.StateChanged -= RefreshStatus;
            _c.ModelChanged -= RefreshStatus;
            _c.SettingsChanged -= RefreshAll;
            _c.LevelChanged -= OnLevel;
            _c.DictationFinished -= OnFinished;
            _clock.Stop();
        };
    }

    void RefreshAll()
    {
        RefreshStatus();
        RefreshStats();
        RefreshRecent();
    }

    void RefreshStatus()
    {
        bool recording = _c.State == DictationState.Recording;
        string colorKey = _c.State == DictationState.Idle && _c.ModelState == ModelState.Ready
            ? Theme.Accent
            : Theme.StatusKey(_c);
        StatusCircle.SetResourceReference(Shape.FillProperty, colorKey);
        StatusRing.SetResourceReference(Shape.FillProperty, colorKey);
        StatusRing.Visibility = recording ? Visibility.Visible : Visibility.Hidden;
        Wave.Visibility = ElapsedText.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording) _clock.Start(); else _clock.Stop();
        ElapsedText.Text = _c.Elapsed.ToString(@"m\:ss");

        (StatusTitle.Text, string before, string after) = _c.State switch
        {
            DictationState.Recording => ("Listening…", "Press", "again to stop and paste."),
            DictationState.Transcribing => ("Transcribing…", "Your text will appear where your cursor is.", ""),
            _ => _c.ModelState switch
            {
                ModelState.Ready => ("Ready to dictate", "Press", "in any app. Press it again to stop and paste."),
                ModelState.Downloading => ($"Downloading speech model… {_c.DownloadedMb} MB",
                    "One-time download. You can already start: press", "and your text is typed once it finishes."),
                ModelState.Failed => ("The speech model didn't load", _c.ModelError ?? "Check the Speech page.", ""),
                _ => ("Loading speech model…", "This takes a few seconds. Shortcut:", ""),
            },
        };

        HintPanel.Children.Clear();
        HintPanel.Children.Add(HintText(before));
        if (_c.State != DictationState.Transcribing && _c.ModelState != ModelState.Failed)
            HintPanel.Children.Add(Theme.Keycaps(_c.Settings.Hotkey));
        if (after.Length > 0) HintPanel.Children.Add(HintText(after));
        if (!_c.HotkeyRegistered && _c.State == DictationState.Idle && _c.ModelState == ModelState.Ready)
            HintPanel.Children.Add(Theme.Pill("Shortcut unavailable. Change it under General.", Theme.CriticalBackground, Theme.Critical));

        var model = AppSettings.Models.FirstOrDefault(m => m.Type == _c.Settings.ModelType && m.Quantization == _c.Settings.ModelQuantization);
        Chips.Children.Clear();
        Chips.Children.Add(Theme.Pill(model?.Name ?? _c.Settings.ModelType.ToString(), Theme.SubtleFill, Theme.TextPrimary));
        if (_c.ModelState == ModelState.Ready)
        {
            bool gpu = _c.RuntimeName.Contains("Vulkan") || _c.RuntimeName.Contains("Cuda");
            Chips.Children.Add(gpu
                ? Theme.Pill($"GPU · {_c.RuntimeName}", Theme.SuccessBackground, Theme.TextPrimary)
                : Theme.Pill("CPU", Theme.SubtleFill, Theme.TextPrimary));
        }
        Chips.Children.Add(Theme.Pill(_c.Settings.Cleanup.Enabled ? "Cleanup on" : "Cleanup off", Theme.SubtleFill, Theme.TextPrimary));

        ActionButton.Content = _c.State switch
        {
            DictationState.Recording => "Stop",
            DictationState.Transcribing => "Working…",
            _ => "Start dictation",
        };
        ActionButton.Appearance = _c.State == DictationState.Idle ? ControlAppearance.Primary : ControlAppearance.Secondary;
        ActionButton.IsEnabled = _c.State != DictationState.Transcribing;
        ActionButton.ToolTip = "Started here, the text is copied to your clipboard. Use the shortcut to type into another app.";
    }

    static TextBlock HintText(string text)
    {
        var t = Theme.Text(text, 14, Theme.TextSecondary);
        t.Margin = new Thickness(0, 0, 6, 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    void RefreshStats()
    {
        DictationsValue.Text = _c.History.Dictations.ToString("N0");
        RemovedValue.Text = _c.History.WordsRemoved.ToString("N0");
        TokensValue.Text = "≈ " + _c.History.EstimatedTokensSaved.ToString("N0");
    }

    void RefreshRecent()
    {
        RecentList.Children.Clear();
        var recent = _c.History.Recent;
        RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < recent.Count; i++) RecentList.Children.Add(RecentCard(recent[i], i));
    }

    Border RecentCard(RecentDictation item, int index)
    {
        bool open = _expanded == index;

        var text = Theme.Text(item.Clean);
        string removed = item.WordsRemoved == 1 ? "1 word removed" : $"{item.WordsRemoved} words removed";
        var meta = Theme.Text($"{Theme.TimeAgo(item.Time)} · {removed}", 12, Theme.TextSecondary);
        meta.Margin = new Thickness(0, 4, 0, 0);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(text);
        texts.Children.Add(meta);

        var toggle = new Button
        {
            Content = open ? "Hide original" : "Show original",
            Icon = new SymbolIcon { Symbol = open ? SymbolRegular.ChevronUp24 : SymbolRegular.ChevronDown24 },
            Margin = new Thickness(16, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Click += (_, _) =>
        {
            _expanded = open ? -1 : index;
            RefreshRecent();
        };

        var copy = new Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 },
            ToolTip = "Copy",
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(copy, "Copy");
        copy.Click += (_, _) =>
        {
            TextInjector.SetClipboard(item.Clean);
            copy.Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24 };
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(texts);
        Grid.SetColumn(toggle, 1);
        Grid.SetColumn(copy, 2);
        row.Children.Add(toggle);
        row.Children.Add(copy);

        var body = new StackPanel();
        body.Children.Add(row);
        if (open)
        {
            var divider = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 10) }.Res(Border.BackgroundProperty, Theme.Divider);
            var label = Theme.Text("What you said", 12, Theme.TextSecondary);
            var diff = Theme.Text("");
            diff.Margin = new Thickness(0, 4, 0, 0);
            diff.LineHeight = 22;
            Theme.ShowDiff(diff, item.Raw, item.Clean);
            body.Children.Add(divider);
            body.Children.Add(label);
            body.Children.Add(diff);
        }

        return new Border { Child = body, Style = (Style)FindResource("Card") };
    }

    void OnLevel(float level)
    {
        if (_c.State == DictationState.Recording) Theme.PushLevel(_bars, level, 26);
    }

    void OnFinished(DictationOutcome outcome)
    {
        if (outcome.Item != null) _expanded = 0;
        RefreshStats();
        RefreshRecent();
    }

    void OnAction(object sender, RoutedEventArgs e) => _c.Toggle();

    void OnClear(object sender, RoutedEventArgs e) => _c.ClearHistory();
}
