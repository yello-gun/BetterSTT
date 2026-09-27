using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace BetterSTT.UI;

public partial class HomePage : Page
{
    readonly DictationController _c = App.Controller;
    readonly Rectangle[] _bars;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    int _expanded = 0;
    string? _fixWord;   // word being fixed in the expanded dictation
    string? _fixSaved;  // confirmation after saving a fix

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
            _c.PendingChanged += RefreshPending;
            RefreshAll();
        };
        Unloaded += (_, _) =>
        {
            _c.StateChanged -= RefreshStatus;
            _c.ModelChanged -= RefreshStatus;
            _c.SettingsChanged -= RefreshAll;
            _c.LevelChanged -= OnLevel;
            _c.DictationFinished -= OnFinished;
            _c.PendingChanged -= RefreshPending;
            _clock.Stop();
        };
    }

    void RefreshAll()
    {
        RefreshStatus();
        RefreshPending();
        RefreshStats();
        RefreshRecent();
    }

    /// <summary>Recordings that failed or were interrupted, each with Retry and Discard.</summary>
    void RefreshPending()
    {
        PendingPanel.Children.Clear();
        var pending = _c.PendingRecordings;
        PendingPanel.Visibility = pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (pending.Count == 0) return;

        var body = new StackPanel();
        var title = Theme.Text(pending.Count == 1
            ? "A dictation wasn't transcribed"
            : $"{pending.Count} dictations weren't transcribed", 14, weight: FontWeights.SemiBold);
        body.Children.Add(title);
        var explain = Theme.Text("The recording was saved. Retry puts the text on your clipboard.", 12, Theme.TextSecondary);
        explain.Margin = new Thickness(0, 2, 0, 8);
        body.Children.Add(explain);

        foreach (string path in pending)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var when = Theme.Text($"Recorded {Theme.TimeAgo(PendingAudio.RecordedAt(path)).ToLowerInvariant()}", 14);
            when.VerticalAlignment = VerticalAlignment.Center;
            var retry = new Button
            {
                Content = "Retry",
                Appearance = ControlAppearance.Primary,
                Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowClockwise24 },
                Margin = new Thickness(8, 0, 8, 0),
                IsEnabled = _c.State == DictationState.Idle,
            };
            retry.Click += async (_, _) => await _c.RetryPendingAsync(path);
            var discard = new Button { Content = "Discard", Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 } };
            discard.Click += (_, _) => _c.DiscardPending(path);

            Grid.SetColumn(retry, 1);
            Grid.SetColumn(discard, 2);
            row.Children.Add(when);
            row.Children.Add(retry);
            row.Children.Add(discard);
            body.Children.Add(row);
        }

        PendingPanel.Children.Add(new Border
        {
            Child = body,
            Style = (Style)FindResource("Card"),
            BorderThickness = new Thickness(1),
        }.Res(Border.BackgroundProperty, Theme.CautionBackground));
    }

    void RefreshStatus()
    {
        bool recording = _c.State == DictationState.Recording;
        bool ready = _c.ModelState is ModelState.Ready or ModelState.Sleeping;
        bool hold = _c.Settings.Activation == ActivationMode.Hold;
        string colorKey = _c.State == DictationState.Idle && ready
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
            DictationState.Recording => hold
                ? ("Listening…", "Release", "to stop and paste. Esc cancels.")
                : ("Listening…", "Press", "again to stop and paste. Esc cancels."),
            DictationState.Transcribing => ("Transcribing…", "Your text will appear where your cursor is.", ""),
            _ => _c.ModelState switch
            {
                ModelState.Ready or ModelState.Sleeping => hold
                    ? ("Ready to dictate", "Hold", "in any app and speak. Let go to paste.")
                    : ("Ready to dictate", "Press", "in any app. Press it again to stop and paste."),
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
        if (!_c.HotkeyRegistered && _c.State == DictationState.Idle && ready)
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
        else if (_c.ModelState == ModelState.Sleeping)
        {
            Chips.Children.Add(Theme.Pill("Model asleep to save memory · wakes when you dictate", Theme.SubtleFill, Theme.TextSecondary));
        }
        // Clicking the cleanup chip switches between cleaned-up text and exact words.
        var cleanup = new Button
        {
            Content = _c.Settings.Cleanup.Enabled ? "Cleanup on" : "Exact words (cleanup off)",
            FontSize = 12,
            Padding = new Thickness(10, 2, 10, 3),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = _c.Settings.Cleanup.Enabled
                ? "Click to type exactly what you say, with no cleanup or word fixes"
                : "Click to turn cleanup back on",
        };
        cleanup.Click += (_, _) => _c.ToggleExactWords();
        Chips.Children.Add(cleanup);

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
        string where = item.AppName != null ? $" · in {item.AppName}" : "";
        var meta = Theme.Text($"{Theme.TimeAgo(item.Time)}{where} · {removed}", 12, Theme.TextSecondary);
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
            _fixWord = _fixSaved = null;
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

            var copyOriginal = new Button
            {
                Content = "Copy original",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 },
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Copy exactly what you said, before cleanup",
            };
            copyOriginal.Click += (_, _) =>
            {
                TextInjector.SetClipboard(item.Raw.Trim());
                copyOriginal.Content = "Copied";
            };
            body.Children.Add(copyOriginal);
            body.Children.Add(FixAWord(item));
        }

        return new Border { Child = body, Style = (Style)FindResource("Card") };
    }

    /// <summary>Click a word that came out wrong, type the right spelling, and it's fixed from then on.</summary>
    StackPanel FixAWord(RecentDictation item)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(Theme.Text("Fix a word: click one that came out wrong", 12, Theme.TextSecondary));

        var words = new WrapPanel { Margin = new Thickness(-4, 4, 0, 0) };
        var distinct = System.Text.RegularExpressions.Regex.Matches(item.Clean, @"[\p{L}\p{N}][\p{L}\p{N}'’-]*")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string word in distinct)
        {
            var chip = new Button
            {
                Content = word,
                Appearance = word == _fixWord ? ControlAppearance.Primary : ControlAppearance.Transparent,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 2, 2),
                MinHeight = 0,
                FontSize = 13,
            };
            chip.Click += (_, _) =>
            {
                _fixWord = word;
                _fixSaved = null;
                RefreshRecent();
            };
            words.Children.Add(chip);
        }
        panel.Children.Add(words);

        if (_fixSaved != null)
        {
            var saved = Theme.Text(_fixSaved, 12, Theme.Success);
            saved.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(saved);
        }
        if (_fixWord == null) return panel;

        // Editor: the heard spelling is editable too, so a phrase like "git hub" can be fixed.
        var editor = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var from = new TextBox { Text = _fixWord, PlaceholderText = "Heard as" };
        var arrow = new SymbolIcon { Symbol = SymbolRegular.ArrowRight24, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(ForegroundProperty, Theme.TextSecondary);
        var to = new TextBox { PlaceholderText = "Should be" };
        System.Windows.Automation.AutomationProperties.SetName(from, "Heard as");
        System.Windows.Automation.AutomationProperties.SetName(to, "Should be");
        var save = new Button { Content = "Save fix", Appearance = ControlAppearance.Primary, Margin = new Thickness(10, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel" };
        var error = Theme.Text("", 12, Theme.Critical);

        void Save()
        {
            if (DictionaryPage.AddFix(_c, from.Text, to.Text) is { } problem)
            {
                error.Text = problem;
                return;
            }
            _fixSaved = $"Saved. From now on “{from.Text.Trim()}” is typed as “{to.Text.Trim()}”. Manage fixes on the Dictionary page.";
            _fixWord = null;
            RefreshRecent();
        }
        save.Click += (_, _) => Save();
        to.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Save(); } };
        cancel.Click += (_, _) => { _fixWord = null; RefreshRecent(); };

        Grid.SetColumn(arrow, 1);
        Grid.SetColumn(to, 2);
        Grid.SetColumn(save, 3);
        Grid.SetColumn(cancel, 4);
        editor.Children.Add(from);
        editor.Children.Add(arrow);
        editor.Children.Add(to);
        editor.Children.Add(save);
        editor.Children.Add(cancel);
        panel.Children.Add(editor);
        panel.Children.Add(error);
        to.Loaded += (_, _) => to.Focus();
        return panel;
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
