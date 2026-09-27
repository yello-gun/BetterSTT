using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace BetterSTT.UI;

public partial class DictionaryPage : Page
{
    readonly DictationController _c = App.Controller;

    readonly System.Windows.Threading.DispatcherTimer _vocabSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _loading;

    public DictionaryPage()
    {
        InitializeComponent();
        FromBox.KeyDown += OnEnter;
        ToBox.KeyDown += OnEnter;
        TriggerBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SnippetTextBox.Focus();
        };

        _vocabSave.Tick += (_, _) => SaveVocabulary();
        VocabularyBox.TextChanged += (_, _) => { if (!_loading) { _vocabSave.Stop(); _vocabSave.Start(); } };
        VocabularyBox.LostFocus += (_, _) => SaveVocabulary();

        Loaded += (_, _) =>
        {
            _loading = true;
            VocabularyBox.Text = _c.Settings.Vocabulary;
            _loading = false;
            Refresh();
            RefreshSnippets();
        };
        Unloaded += (_, _) => { if (_vocabSave.IsEnabled) SaveVocabulary(); };
    }

    void SaveVocabulary()
    {
        _vocabSave.Stop();
        string text = VocabularyBox.Text.Trim();
        if (text != _c.Settings.Vocabulary) _c.Update(s => s.Vocabulary = text);
    }

    void OnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAdd(sender, e);
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        string? problem = AddFix(_c, FromBox.Text, ToBox.Text);
        AddError.Text = problem ?? "";
        AddError.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        if (problem != null) return;
        FromBox.Text = ToBox.Text = "";
        FromBox.Focus();
        Refresh();
    }

    /// <summary>Adds (or updates) a word fix. Returns why it can't be added, or null. Shared with Home.</summary>
    public static string? AddFix(DictationController c, string from, string to)
    {
        from = from.Trim();
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0) return "Fill in both what it heard and what it should type.";
        if (string.Equals(from, to, StringComparison.Ordinal)) return "The two spellings are the same.";

        c.Update(s =>
        {
            var existing = s.Replacements.FirstOrDefault(r => string.Equals(r.From.Trim(), from, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.To = to;
            else s.Replacements.Add(new Replacement { From = from, To = to });
        });
        return null;
    }

    void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "BetterSTT-dictionary.json",
            Filter = "BetterSTT dictionary|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, DictionaryTransfer.Export(_c.Settings));
            ShowTransfer($"Saved {Path.GetFileName(dialog.FileName)}.", error: false);
        }
        catch (Exception ex)
        {
            ShowTransfer($"Couldn't save it: {ex.Message}", error: true);
        }
    }

    void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Dictionary or list|*.json;*.csv;*.txt|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            string text = File.ReadAllText(dialog.FileName);
            var copy = _c.Settings.Clone();
            var result = DictionaryTransfer.Import(text, copy); // checked on a copy first, so a bad file changes nothing
            _c.Update(s => DictionaryTransfer.Import(text, s));
            ShowTransfer($"Imported {result}.", error: false);
            _loading = true;
            VocabularyBox.Text = _c.Settings.Vocabulary;
            _loading = false;
            Refresh();
            RefreshSnippets();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            ShowTransfer($"Couldn't import it: {ex.Message}", error: true);
        }
    }

    void ShowTransfer(string message, bool error)
    {
        TransferText.Text = message;
        TransferText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error ? Theme.Critical : Theme.TextSecondary);
        TransferText.Visibility = Visibility.Visible;
    }

    void OnAddSnippet(object sender, RoutedEventArgs e)
    {
        string trigger = TriggerBox.Text.Trim(), text = SnippetTextBox.Text.Trim();
        string? problem = trigger.Length == 0 || text.Length == 0
            ? "Fill in both the trigger and the text to type."
            : !trigger.Any(char.IsLetterOrDigit) ? "The trigger needs at least one word." : null;
        SnippetError.Text = problem ?? "";
        SnippetError.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        if (problem != null) return;

        _c.Update(s =>
        {
            var existing = s.Snippets.FirstOrDefault(x => string.Equals(x.Trigger.Trim(), trigger, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Text = text;
            else s.Snippets.Add(new Snippet { Trigger = trigger, Text = text });
        });
        TriggerBox.Text = SnippetTextBox.Text = "";
        TriggerBox.Focus();
        RefreshSnippets();
    }

    void RefreshSnippets()
    {
        SnippetList.Children.Clear();
        var snippets = _c.Settings.Snippets;
        SnippetEmptyText.Visibility = snippets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnippetCountText.Text = snippets.Count == 1 ? "1 snippet" : $"{snippets.Count} snippets";

        foreach (var snippet in snippets.OrderBy(x => x.Trigger, StringComparer.CurrentCultureIgnoreCase).ToList())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var trigger = Theme.Text($"“{snippet.Trigger}”", 14, Theme.TextSecondary);
            trigger.VerticalAlignment = VerticalAlignment.Center;
            var arrow = new SymbolIcon { Symbol = SymbolRegular.ArrowRight24, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            arrow.SetResourceReference(ForegroundProperty, Theme.TextSecondary);
            var text = Theme.Text(snippet.Text, 14);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextWrapping = TextWrapping.Wrap;
            var remove = new Button
            {
                Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 },
                Appearance = ControlAppearance.Transparent,
                ToolTip = "Remove this snippet",
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove snippet {snippet.Trigger}");
            remove.Click += (_, _) =>
            {
                _c.Update(s => s.Snippets.RemoveAll(x => x.Trigger == snippet.Trigger && x.Text == snippet.Text));
                RefreshSnippets();
            };

            Grid.SetColumn(arrow, 1);
            Grid.SetColumn(text, 2);
            Grid.SetColumn(remove, 3);
            row.Children.Add(trigger);
            row.Children.Add(arrow);
            row.Children.Add(text);
            row.Children.Add(remove);
            SnippetList.Children.Add(new Border { Child = row, Style = (Style)FindResource("Card"), Padding = new Thickness(20, 8, 12, 8) });
        }
    }

    void Refresh()
    {
        List.Children.Clear();
        var fixes = _c.Settings.Replacements;
        EmptyText.Visibility = fixes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = fixes.Count == 1 ? "1 fix" : $"{fixes.Count} fixes";

        foreach (var fix in fixes.OrderBy(r => r.To, StringComparer.CurrentCultureIgnoreCase).ToList())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var from = Theme.Text(fix.From, 14, Theme.TextSecondary);
            from.VerticalAlignment = VerticalAlignment.Center;
            var arrow = new SymbolIcon { Symbol = SymbolRegular.ArrowRight24, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            arrow.SetResourceReference(ForegroundProperty, Theme.TextSecondary);
            var to = Theme.Text(fix.To, 14, weight: FontWeights.SemiBold);
            to.VerticalAlignment = VerticalAlignment.Center;
            var remove = new Button
            {
                Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 },
                Appearance = ControlAppearance.Transparent,
                ToolTip = "Remove this fix",
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove fix {fix.From} to {fix.To}");
            remove.Click += (_, _) =>
            {
                _c.Update(s => s.Replacements.RemoveAll(r => r.From == fix.From && r.To == fix.To));
                Refresh();
            };

            Grid.SetColumn(arrow, 1);
            Grid.SetColumn(to, 2);
            Grid.SetColumn(remove, 3);
            row.Children.Add(from);
            row.Children.Add(arrow);
            row.Children.Add(to);
            row.Children.Add(remove);
            List.Children.Add(new Border { Child = row, Style = (Style)FindResource("Card"), Padding = new Thickness(20, 8, 12, 8) });
        }
    }
}
