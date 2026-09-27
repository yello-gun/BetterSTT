using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace BetterSTT.UI;

/// <summary>
/// Opened by the add-a-word shortcut: adds the word selected in any app to the dictionary, either as a
/// spelling to learn or as a fix that always replaces it.
/// </summary>
public sealed class AddWordWindow : FluentWindow
{
    readonly DictationController _c = App.Controller;
    readonly TextBox _word, _replacement;
    readonly RadioButton _learn, _replace;
    readonly TextBlock _error;

    public AddWordWindow(string? selected)
    {
        Title = "Add to dictionary";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        this.SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

        var body = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        var heading = Theme.Text("Add to dictionary", 18, weight: FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 0, 0, 4);
        body.Children.Add(heading);
        body.Children.Add(Caption(selected == null
            ? "Nothing was selected. Type the word or name here."
            : "The word you selected:"));

        _word = new TextBox { Text = selected ?? "", PlaceholderText = "Word or name, e.g. Kubernetes", Margin = new Thickness(0, 6, 0, 14) };
        System.Windows.Automation.AutomationProperties.SetName(_word, "Word");
        body.Children.Add(_word);

        _learn = new RadioButton { Content = "Learn this spelling", IsChecked = true, GroupName = "mode" };
        body.Children.Add(_learn);
        var learnHint = Caption("It's given to the speech model as a hint, so it hears and spells the word this way.");
        learnHint.Margin = new Thickness(28, 0, 0, 10);
        body.Children.Add(learnHint);

        _replace = new RadioButton { Content = "Always replace it with", GroupName = "mode" };
        body.Children.Add(_replace);
        _replacement = new TextBox { PlaceholderText = "The right spelling, e.g. GitHub", Margin = new Thickness(28, 6, 0, 0), IsEnabled = false };
        System.Windows.Automation.AutomationProperties.SetName(_replacement, "Replace it with");
        body.Children.Add(_replacement);
        _replace.Checked += (_, _) => { _replacement.IsEnabled = true; _replacement.Focus(); };
        _learn.Checked += (_, _) => _replacement.IsEnabled = false;

        _error = Caption("");
        _error.Res(TextBlock.ForegroundProperty, Theme.Critical);
        _error.Margin = new Thickness(0, 10, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        body.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var add = new Button { Content = "Add", Appearance = ControlAppearance.Primary, MinWidth = 90, IsDefault = true };
        add.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(add);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleBar = new TitleBar { Title = "BetterSTT", ShowMaximize = false, ShowMinimize = false };
        root.Children.Add(titleBar);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) =>
        {
            Activate();
            if (selected == null) _word.Focus();
            else add.Focus();
        };
    }

    static TextBlock Caption(string text) => Theme.Text(text, 12, Theme.TextSecondary);

    void Save()
    {
        string word = _word.Text.Trim();
        if (word.Length == 0)
        {
            ShowError("Type the word or name first.");
            return;
        }
        if (_replace.IsChecked == true)
        {
            if (DictionaryPage.AddFix(_c, word, _replacement.Text) is { } problem)
            {
                ShowError(problem);
                return;
            }
            _c.Announce($"From now on, “{word}” is typed as “{_replacement.Text.Trim()}”.");
        }
        else
        {
            _c.Update(s =>
            {
                var words = s.Vocabulary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (!words.Contains(word, StringComparer.OrdinalIgnoreCase)) words.Add(word);
                s.Vocabulary = string.Join(", ", words);
            });
            _c.Announce($"Added “{word}” to the words BetterSTT should know.");
        }
        Close();
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }
}
