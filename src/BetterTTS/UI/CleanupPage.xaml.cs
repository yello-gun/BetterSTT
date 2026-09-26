using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace BetterTTS.UI;

public partial class CleanupPage : Page
{
    readonly DictationController _c = App.Controller;
    bool _building;

    /// <summary>One card per cleanup rule: how to read/write its switch and (optionally) its word list.</summary>
    sealed record Rule(
        string Glyph, string Title, string Description,
        Func<CleanupOptions, bool> Get, Action<CleanupOptions, bool> Set,
        string? ListLabel = null,
        Func<CleanupOptions, List<string>>? Words = null);

    static readonly Rule[] RuleDefs =
    [
        new("um", "Filler sounds", "Removes um, uh, hmm and similar sounds, including stretched ones like “ummm”.",
            o => o.RemoveFillerSounds, (o, v) => o.RemoveFillerSounds = v, "Words", o => o.FillerSounds),
        new("…", "Pause marks", "Removes the “...” that appears when you pause mid-sentence.",
            o => o.RemoveEllipses, (o, v) => o.RemoveEllipses = v),
        new("like", "Verbal fillers", "Only when they stand alone between commas: “It was, like, huge” → “It was huge”. “I like pizza” is kept.",
            o => o.RemoveDiscourseMarkers, (o, v) => o.RemoveDiscourseMarkers = v, "Phrases", o => o.DiscourseMarkers),
        new("I-I", "Stutters and repeats", "“I-I think the the plan” → “I think the plan”.",
            o => o.RemoveStutters, (o, v) => o.RemoveStutters = v, "Keep repeats of", o => o.StutterExceptions),
        new("[ ]", "Non-speech tags", "Removes [BLANK_AUDIO], (music) and similar notes the model adds.",
            o => o.RemoveNonSpeechTags, (o, v) => o.RemoveNonSpeechTags = v),
    ];

    public CleanupPage()
    {
        InitializeComponent();
        MasterSwitch.Checked += (_, _) => SetMaster(true);
        MasterSwitch.Unchecked += (_, _) => SetMaster(false);
        SampleBox.TextChanged += (_, _) => RefreshPreview();
        Loaded += (_, _) => Build();
    }

    void Build()
    {
        _building = true;
        MasterSwitch.IsChecked = _c.Settings.Cleanup.Enabled;
        _building = false;

        Rules.Children.Clear();
        foreach (var rule in RuleDefs) Rules.Children.Add(RuleCard(rule));
        Rules.IsEnabled = _c.Settings.Cleanup.Enabled;
        Rules.Opacity = Rules.IsEnabled ? 1 : 0.5;
        RefreshPreview();
    }

    void SetMaster(bool on)
    {
        if (_building) return;
        _c.Update(s => s.Cleanup.Enabled = on);
        Rules.IsEnabled = on;
        Rules.Opacity = on ? 1 : 0.5;
        RefreshPreview();
    }

    Border RuleCard(Rule rule)
    {
        var options = _c.Settings.Cleanup;

        var glyph = new Border
        {
            Style = (Style)FindResource("IconTile"),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = rule.Glyph,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }.Res(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush"),
        };

        var texts = new StackPanel { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Theme.Text(rule.Title));
        var desc = Theme.Text(rule.Description, 12, Theme.TextSecondary);
        desc.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(desc);

        var toggle = new ToggleSwitch { IsChecked = rule.Get(options), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(toggle, rule.Title);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(glyph);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(toggle, 2);
        header.Children.Add(texts);
        header.Children.Add(toggle);

        var body = new StackPanel();
        body.Children.Add(header);

        WrapPanel? chips = null;
        if (rule.Words != null)
        {
            chips = new WrapPanel { Margin = new Thickness(56, 12, 0, 0) };
            FillChips(chips, rule);
            chips.Visibility = rule.Get(options) ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(chips);
        }

        toggle.Checked += (_, _) => OnRuleToggled(rule, true, chips);
        toggle.Unchecked += (_, _) => OnRuleToggled(rule, false, chips);

        return new Border { Child = body, Style = (Style)FindResource("Card"), Padding = new Thickness(20, 16, 20, 16) };
    }

    void OnRuleToggled(Rule rule, bool on, WrapPanel? chips)
    {
        _c.Update(s => rule.Set(s.Cleanup, on));
        if (chips != null) chips.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
    }

    void FillChips(WrapPanel panel, Rule rule)
    {
        panel.Children.Clear();
        var label = Theme.Text(rule.ListLabel!, 12, Theme.TextSecondary);
        label.Margin = new Thickness(0, 0, 10, 6);
        label.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(label);

        foreach (var word in rule.Words!(_c.Settings.Cleanup).ToList())
        {
            var remove = new Button
            {
                Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss12, FontSize = 10 },
                Appearance = ControlAppearance.Transparent,
                Padding = new Thickness(4),
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = 0,
                MinHeight = 0,
                BorderThickness = new Thickness(0),
                ToolTip = $"Remove “{word}”",
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove {word}");
            remove.Click += (_, _) =>
            {
                _c.Update(s => rule.Words!(s.Cleanup).RemoveAll(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)));
                FillChips(panel, rule);
                RefreshPreview();
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var text = Theme.Text(word, 13);
            text.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(text);
            content.Children.Add(remove);

            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 2, 4, 2),
                Margin = new Thickness(0, 0, 6, 6),
                Child = content,
            }.Res(Border.BackgroundProperty, Theme.ControlFill).Res(Border.BorderBrushProperty, Theme.ControlStroke));
        }

        var add = new TextBox
        {
            PlaceholderText = "Add word",
            Width = 130,
            Margin = new Thickness(0, 0, 6, 6),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Add16 },
        };
        add.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            string word = add.Text.Trim();
            var list = rule.Words!(_c.Settings.Cleanup);
            if (word.Length > 0 && !list.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                _c.Update(s => rule.Words!(s.Cleanup).Add(word));
                FillChips(panel, rule);
                RefreshPreview();
                if (panel.Children[^1] is TextBox box) box.Focus();
            }
            e.Handled = true;
        };
        panel.Children.Add(add);
    }

    void RefreshPreview()
    {
        if (DiffText == null) return;
        string raw = SampleBox.Text;
        string clean = TextCleaner.Clean(raw, _c.Settings.Cleanup);
        Theme.ShowDiff(DiffText, raw, clean);
        ResultText.Text = clean.Length > 0 ? clean : "(nothing left)";
        int removed = Math.Max(0, TextCleaner.CountWords(raw) - TextCleaner.CountWords(clean));
        SavedText.Text = removed == 1 ? "1 word removed" : $"{removed} words removed";
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        _c.Update(s => s.Cleanup = new CleanupOptions());
        Build();
    }
}
