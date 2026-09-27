using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace BetterSTT.UI;

/// <summary>Writing styles (pick, edit, create) plus the cleanup rules every style shares.</summary>
public partial class StylePage : Page
{
    readonly DictationController _c = App.Controller;

    const string SampleSpeech =
        "Um, hi Sarah, so I was thinking... like, maybe we could, uh, meet on Tuesday or something. " +
        "I think I think the the new approach is better. Also, can you send the slides and stuff? Thanks.";

    const string MathSample = "Um, so x squared plus two x plus one equals open parenthesis x plus one close parenthesis squared.";

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
        new("I-I", "Stutters and restarts",
            "Repeated words and phrases (“I think I think”, “the the”) and cut-off words (“we were go- going”).",
            o => o.RemoveStutters, (o, v) => o.RemoveStutters = v, "Keep repeats of", o => o.StutterExceptions),
        new("↩", "Mid-sentence corrections",
            "When you correct yourself, only the correction is kept: “Tuesday, no wait, Wednesday” → “Wednesday”, “Buy milk. Scratch that. Buy eggs.” → “Buy eggs.” What gets replaced is worked out from what you said before and after, and “sorry” or “I mean” are left alone when they aren't a correction.",
            o => o.FixCorrections, (o, v) => o.FixCorrections = v),
        new("[ ]", "Non-speech tags", "Removes [BLANK_AUDIO], (music) and similar notes the model adds.",
            o => o.RemoveNonSpeechTags, (o, v) => o.RemoveNonSpeechTags = v),
    ];

    public StylePage()
    {
        InitializeComponent();
        SampleBox.Text = SampleSpeech;
        SampleBox.TextChanged += (_, _) => RefreshPreview();
        Loaded += (_, _) =>
        {
            RefreshStyles();
            Rules.Children.Clear();
            foreach (var rule in RuleDefs) Rules.Children.Add(RuleCard(rule));
            RefreshPreview();
        };
    }

    WritingStyle Current => _c.Settings.CurrentStyle;

    // ---- styles ----

    void RefreshStyles()
    {
        // The "Try it" box shows math for a math style, as long as it still holds one of the samples.
        if (SampleBox.Text is SampleSpeech or MathSample)
            SampleBox.Text = Current.SpokenMath != MathFormat.Off ? MathSample : SampleSpeech;
        StyleCards.Children.Clear();
        foreach (var style in _c.Settings.Styles) StyleCards.Children.Add(StyleCard(style));
        BuildEditor();
        RefreshPreview();
    }

    Button StyleCard(WritingStyle style)
    {
        bool selected = string.Equals(style.Name, Current.Name, StringComparison.OrdinalIgnoreCase);
        var radio = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var ring = new Ellipse { StrokeThickness = selected ? 5 : 1 };
        ring.SetResourceReference(Shape.StrokeProperty, selected ? Theme.Accent : Theme.StrongStroke);
        radio.Children.Add(ring);

        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), Width = 180 };
        texts.Children.Add(Theme.Text(style.Name, 14, weight: FontWeights.SemiBold));
        var desc = Theme.Text(string.IsNullOrWhiteSpace(style.Description) ? "Your own style." : style.Description, 12, Theme.TextSecondary);
        desc.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(desc);

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(radio);
        content.Children.Add(texts);

        var card = new Button
        {
            Content = content,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 8, 8),
            MinHeight = 86,
            VerticalContentAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(selected ? 2 : 1),
        };
        card.Res(Control.BackgroundProperty, Theme.CardFill).Res(Control.BorderBrushProperty, selected ? Theme.Accent : Theme.CardStroke);
        System.Windows.Automation.AutomationProperties.SetName(card, $"{style.Name} style{(selected ? ", selected" : "")}");
        string name = style.Name;
        card.Click += (_, _) =>
        {
            _c.SetStyle(name);
            RefreshStyles();
        };
        return card;
    }

    void OnNewStyle(object sender, RoutedEventArgs e)
    {
        // A new style starts as a copy of the selected one (or Natural, if Exact words is selected).
        var source = Current.ExactWords ? _c.Settings.StyleNamed(WritingStyle.NaturalName) : Current;
        string name = "My style";
        for (int i = 2; _c.Settings.Styles.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = $"My style {i}";
        _c.Update(s =>
        {
            s.Styles.Add(new WritingStyle
            {
                Name = name,
                Description = "Your own style.",
                RemoveVagueEndings = source.RemoveVagueEndings,
                VagueEndings = source.VagueEndings.ToList(),
                AutoParagraphs = source.AutoParagraphs,
                MaxSentencesPerParagraph = source.MaxSentencesPerParagraph,
                GreetingAndSignOffLines = source.GreetingAndSignOffLines,
                ParagraphStarters = source.ParagraphStarters.ToList(),
                SpokenMath = source.SpokenMath,
                SpokenLayout = source.SpokenLayout,
                SpokenPunctuation = source.SpokenPunctuation,
            });
            s.Style = name;
        });
        RefreshStyles();
    }

    /// <summary>Edits the current style, looked up by name at the time the change is applied.</summary>
    void EditStyle(Action<WritingStyle> change)
    {
        string name = Current.Name;
        _c.Update(s => change(s.StyleNamed(name)));
        RefreshPreview();
    }

    void BuildEditor()
    {
        Editor.Children.Clear();
        var style = Current;
        EditorCard.Visibility = Visibility.Visible;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Theme.Text($"Edit “{style.Name}”", 14, weight: FontWeights.SemiBold));
        if (!style.BuiltIn)
        {
            var delete = new Button { Content = "Delete style", Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 } };
            delete.Click += (_, _) =>
            {
                string name = style.Name;
                _c.Update(s =>
                {
                    s.Styles.RemoveAll(x => x.Name == name);
                    if (s.Style == name) s.Style = WritingStyle.NaturalName;
                    foreach (var p in s.AppProfiles.Where(p => p.Style == name)) p.Style = null;
                });
                RefreshStyles();
            };
            Grid.SetColumn(delete, 1);
            header.Children.Add(delete);
        }
        Editor.Children.Add(header);

        if (style.ExactWords)
        {
            Editor.Children.Add(Hint("Exact words types what was heard, with no cleanup, no word fixes and no formatting. There's nothing to set up."));
            return;
        }

        if (!style.BuiltIn)
        {
            var nameBox = new TextBox { Text = style.Name, PlaceholderText = "Style name", Margin = new Thickness(0, 12, 0, 0), MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetName(nameBox, "Style name");
            nameBox.LostFocus += (_, _) => Rename(style.Name, nameBox.Text);
            nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Rename(style.Name, nameBox.Text); };
            Editor.Children.Add(nameBox);
        }

        string styleName = style.Name;
        Func<AppSettings, List<string>> vague = s => s.StyleNamed(styleName).VagueEndings;
        Func<AppSettings, List<string>> starters = s => s.StyleNamed(styleName).ParagraphStarters;

        // Vague endings
        Editor.Children.Add(Option("Remove vague endings",
            "Drops tails that add no information, like “or something” and “and stuff”: “grab lunch or something” → “grab lunch”.",
            style.RemoveVagueEndings, on => EditStyle(s => s.RemoveVagueEndings = on),
            WordChips("Endings", vague)));

        // Paragraphs
        var paragraphDetails = new StackPanel();
        var maxRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        var maxLabel = Theme.Text("Start a new paragraph after at most", 13, Theme.TextSecondary);
        maxLabel.VerticalAlignment = VerticalAlignment.Center;
        var maxBox = new ComboBox { ItemsSource = new[] { 2, 3, 4, 5, 6, 8 }, SelectedItem = style.MaxSentencesPerParagraph, Margin = new Thickness(8, 0, 8, 0), MinWidth = 70 };
        if (maxBox.SelectedIndex < 0) maxBox.SelectedItem = 4;
        System.Windows.Automation.AutomationProperties.SetName(maxBox, "Sentences per paragraph");
        maxBox.SelectionChanged += (_, _) => { if (maxBox.SelectedItem is int n) EditStyle(s => s.MaxSentencesPerParagraph = n); };
        var sentences = Theme.Text("sentences", 13, Theme.TextSecondary);
        sentences.VerticalAlignment = VerticalAlignment.Center;
        maxRow.Children.Add(maxLabel);
        maxRow.Children.Add(maxBox);
        maxRow.Children.Add(sentences);
        paragraphDetails.Children.Add(maxRow);

        var greetings = new ToggleSwitch
        {
            IsChecked = style.GreetingAndSignOffLines,
            Content = "Put greetings (“Hi John,”) and sign-offs (“Thanks,”) on their own lines",
            Margin = new Thickness(0, 0, 0, 8),
        };
        greetings.Checked += (_, _) => EditStyle(s => s.GreetingAndSignOffLines = true);
        greetings.Unchecked += (_, _) => EditStyle(s => s.GreetingAndSignOffLines = false);
        paragraphDetails.Children.Add(greetings);
        paragraphDetails.Children.Add(WordChips("New paragraph at", starters));

        Editor.Children.Add(Option("Automatic paragraphs",
            "Starts a new paragraph, with a blank line before it, when you change topic (“Also…”, “Next…”), after a greeting, before a sign-off, and when a paragraph gets long.",
            style.AutoParagraphs, on => EditStyle(s => s.AutoParagraphs = on), paragraphDetails));

        // Spoken commands
        Editor.Children.Add(Option("Spoken new lines",
            "Say “new line” or “new paragraph” as its own phrase to start one. “Add a new line to the file” is left as words.",
            style.SpokenLayout, on => EditStyle(s => s.SpokenLayout = on), new StackPanel()));
        Editor.Children.Add(Option("Spoken punctuation",
            "Say “comma”, “period”, “question mark”, “exclamation point”, “colon”, “semicolon”, “open quote” and “close quote” to type them. Useful if you like to control punctuation yourself.",
            style.SpokenPunctuation, on => EditStyle(s => s.SpokenPunctuation = on), new StackPanel()));

        // Spoken math
        var mathDetails = new StackPanel { Orientation = Orientation.Horizontal };
        var formatLabel = Theme.Text("Write it as", 13, Theme.TextSecondary);
        formatLabel.VerticalAlignment = VerticalAlignment.Center;
        var formatBox = new ComboBox
        {
            ItemsSource = new[] { "Symbols: x² + 1/2", @"LaTeX: x^2 + \frac{1}{2}" },
            SelectedIndex = style.SpokenMath == MathFormat.Latex ? 1 : 0,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 220,
        };
        System.Windows.Automation.AutomationProperties.SetName(formatBox, "Math format");
        formatBox.SelectionChanged += (_, _) =>
            EditStyle(s => s.SpokenMath = formatBox.SelectedIndex == 1 ? MathFormat.Latex : MathFormat.Symbols);
        mathDetails.Children.Add(formatLabel);
        mathDetails.Children.Add(formatBox);
        Editor.Children.Add(Option("Spoken math",
            "Writes math you say as symbols: numbers, plus, minus, times, over, equals, squared, to the power of, square root, pi, Greek letters and brackets. Number words stay words unless math is said with them.",
            style.SpokenMath != MathFormat.Off,
            on => EditStyle(s => s.SpokenMath = on ? (formatBox.SelectedIndex == 1 ? MathFormat.Latex : MathFormat.Symbols) : MathFormat.Off),
            mathDetails));
    }

    void Rename(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == oldName) return;
        if (_c.Settings.Styles.Any(s => string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase))) return;
        _c.Update(s =>
        {
            s.StyleNamed(oldName).Name = newName;
            if (s.Style == oldName) s.Style = newName;
            foreach (var p in s.AppProfiles.Where(p => p.Style == oldName)) p.Style = newName;
        });
        RefreshStyles();
    }

    /// <summary>A switch with a title and description, and details shown only while it's on.</summary>
    static StackPanel Option(string title, string description, bool on, Action<bool> changed, FrameworkElement details)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel();
        texts.Children.Add(Theme.Text(title));
        var desc = Theme.Text(description, 12, Theme.TextSecondary);
        desc.Margin = new Thickness(0, 2, 16, 0);
        texts.Children.Add(desc);
        var toggle = new ToggleSwitch { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(toggle, title);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(texts);
        row.Children.Add(toggle);

        details.Margin = new Thickness(0, 10, 0, 0);
        details.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        toggle.Checked += (_, _) => { details.Visibility = Visibility.Visible; changed(true); };
        toggle.Unchecked += (_, _) => { details.Visibility = Visibility.Collapsed; changed(false); };

        panel.Children.Add(row);
        panel.Children.Add(details);
        return panel;
    }

    static TextBlock Hint(string text)
    {
        var t = Theme.Text(text, 12, Theme.TextSecondary);
        t.Margin = new Thickness(0, 8, 0, 0);
        return t;
    }

    // ---- cleanup rules ----

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

        FrameworkElement? chips = null;
        if (rule.Words != null)
        {
            chips = WordChips(rule.ListLabel!, s => rule.Words(s.Cleanup));
            chips.Margin = new Thickness(56, 12, 0, 0);
            chips.Visibility = rule.Get(options) ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(chips);
        }

        toggle.Checked += (_, _) => OnRuleToggled(rule, true, chips);
        toggle.Unchecked += (_, _) => OnRuleToggled(rule, false, chips);

        return new Border { Child = body, Style = (Style)FindResource("Card"), Padding = new Thickness(20, 16, 20, 16) };
    }

    void OnRuleToggled(Rule rule, bool on, FrameworkElement? chips)
    {
        _c.Update(s => rule.Set(s.Cleanup, on));
        if (chips != null) chips.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
    }

    /// <summary>An editable list of words as removable chips, with an "Add word" box.</summary>
    WrapPanel WordChips(string label, Func<AppSettings, List<string>> list)
    {
        var panel = new WrapPanel();
        void Fill()
        {
            panel.Children.Clear();
            var caption = Theme.Text(label, 12, Theme.TextSecondary);
            caption.Margin = new Thickness(0, 0, 10, 6);
            caption.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(caption);

            foreach (var word in list(_c.Settings).ToList())
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
                    _c.Update(s => list(s).RemoveAll(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)));
                    Fill();
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

            var add = new TextBox { PlaceholderText = "Add", Width = 130, Margin = new Thickness(0, 0, 6, 6), Icon = new SymbolIcon { Symbol = SymbolRegular.Add16 } };
            System.Windows.Automation.AutomationProperties.SetName(add, $"Add to {label}");
            add.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                string word = add.Text.Trim();
                if (word.Length == 0 || list(_c.Settings).Contains(word, StringComparer.OrdinalIgnoreCase)) return;
                _c.Update(s => list(s).Add(word));
                Fill();
                RefreshPreview();
                if (panel.Children[^1] is TextBox box) box.Focus();
            };
            panel.Children.Add(add);
        }
        Fill();
        return panel;
    }

    // ---- preview ----

    void RefreshPreview()
    {
        if (DiffText == null) return;
        string raw = SampleBox.Text;
        var s = _c.Settings;
        string clean = TextCleaner.Process(raw, s);
        Theme.ShowDiff(DiffText, raw, clean);
        ResultText.Text = clean.Length > 0 ? clean : "(nothing left)";
        int removed = Math.Max(0, TextCleaner.CountWords(raw) - TextCleaner.CountWords(clean));
        SavedText.Text = removed == 1 ? "1 word removed" : $"{removed} words removed";
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        _c.Update(s => s.Cleanup = new CleanupOptions());
        Rules.Children.Clear();
        foreach (var rule in RuleDefs) Rules.Children.Add(RuleCard(rule));
        RefreshPreview();
    }
}
