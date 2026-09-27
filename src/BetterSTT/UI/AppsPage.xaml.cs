using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BetterSTT.UI;

public partial class AppsPage : Page
{
    readonly DictationController _c = App.Controller;

    sealed record Candidate(AppInfo App, string Label)
    {
        // Screen readers announce list items by ToString.
        public override string ToString() => Label;
    }

    public AppsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshPicker();
            RefreshList();
        };
    }

    void OnRefreshApps(object sender, RoutedEventArgs e) => RefreshPicker();

    /// <summary>Apps you recently dictated into first, then everything with an open window, minus apps that already have rules.</summary>
    void RefreshPicker()
    {
        var existing = new HashSet<string>(_c.Settings.AppProfiles.Select(p => p.ProcessName), StringComparer.OrdinalIgnoreCase);
        var recent = _c.History.Recent
            .Where(r => r.AppProcess != null)
            .Select(r => new AppInfo(r.AppProcess!, r.AppName ?? r.AppProcess!));
        var open = ForegroundApp.WithWindows();

        var candidates = recent.Select(a => new Candidate(a, $"{a.DisplayName}  (recently dictated into)"))
            .Concat(open.Select(a => new Candidate(a, $"{a.DisplayName}  ({a.ProcessName})")))
            .Where(c => !existing.Contains(c.App.ProcessName))
            .DistinctBy(c => c.App.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AppPicker.ItemsSource = candidates;
        AppPicker.SelectedIndex = candidates.Count > 0 ? 0 : -1;
        AppPicker.IsEnabled = candidates.Count > 0;
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (AppPicker.SelectedItem is not Candidate c) return;
        _c.Update(s => s.AppProfiles.Add(new AppProfile { ProcessName = c.App.ProcessName, DisplayName = c.App.DisplayName }));
        RefreshPicker();
        RefreshList();
    }

    void RefreshList()
    {
        List.Children.Clear();
        var profiles = _c.Settings.AppProfiles;
        EmptyText.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var profile in profiles.ToList()) List.Children.Add(ProfileCard(profile));
    }

    Border ProfileCard(AppProfile profile)
    {
        string process = profile.ProcessName;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel();
        names.Children.Add(Theme.Text(string.IsNullOrWhiteSpace(profile.DisplayName) ? process : profile.DisplayName, 14, weight: FontWeights.SemiBold));
        names.Children.Add(Theme.Text($"{process}.exe", 12, Theme.TextSecondary));
        var remove = new Button { Content = "Remove", Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 }, VerticalAlignment = VerticalAlignment.Top };
        remove.Click += (_, _) =>
        {
            _c.Update(s => s.AppProfiles.RemoveAll(p => p.ProcessName == process));
            RefreshPicker();
            RefreshList();
        };
        Grid.SetColumn(remove, 1);
        header.Children.Add(names);
        header.Children.Add(remove);

        // Each option edits the saved profile directly (looked up fresh, since Update may run later).
        void Edit(Action<AppProfile> change) =>
            _c.Update(s => { if (s.ProfileFor(process) is { } p) change(p); });

        var options = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        // "Usual" follows the style chosen on the Style page; the rest are the styles by name.
        var styleNames = _c.Settings.Styles.Select(s => s.Name).ToList();
        int styleIndex = profile.Style == null ? 0 : styleNames.FindIndex(n => string.Equals(n, profile.Style, StringComparison.OrdinalIgnoreCase)) + 1;
        options.Children.Add(Option("Writing style", ["Usual", .. styleNames],
            Math.Max(0, styleIndex),
            i => Edit(p => p.Style = i == 0 ? null : styleNames[i - 1])));
        options.Children.Add(Option("Space after each dictation", ["Usual", "Add a space", "No space"],
            profile.AddTrailingSpace switch { null => 0, true => 1, false => 2 },
            i => Edit(p => p.AddTrailingSpace = i switch { 1 => true, 2 => false, _ => null })));
        options.Children.Add(Option("How text is entered", ["Usual", "Paste", "Type characters"],
            profile.OutputMethod switch { null => 0, OutputMethod.Paste => 1, _ => 2 },
            i => Edit(p => p.OutputMethod = i switch { 1 => OutputMethod.Paste, 2 => OutputMethod.Type, _ => null })));
        // Languages the app's model can hear; "Usual" follows the Speech page.
        var languages = AppSettings.Languages;
        int languageIndex = profile.Language == null ? 0 : Array.FindIndex(languages, l => l.Code == profile.Language) + 1;
        options.Children.Add(Option("Language you speak", ["Usual", .. languages.Select(l => l.Name)],
            Math.Max(0, languageIndex),
            i => Edit(p => p.Language = i == 0 ? null : languages[i - 1].Code)));
        options.Children.Add(Option("Line breaks", ["Keep", "Turn into spaces"],
            profile.JoinLines == true ? 1 : 0,
            i => Edit(p => p.JoinLines = i == 1 ? true : null)));
        options.Children.Add(Option("Listening indicator", ["Usual", "Show", "Hide"],
            profile.ShowOverlay switch { null => 0, true => 1, false => 2 },
            i => Edit(p => p.ShowOverlay = i switch { 1 => true, 2 => false, _ => null })));

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(options);
        return new Border { Child = body, Style = (Style)FindResource("Card"), Padding = new Thickness(20, 16, 20, 16) };
    }

    static StackPanel Option(string label, string[] choices, int selected, Action<int> changed)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 16, 8) };
        var text = Theme.Text(label, 12, Theme.TextSecondary);
        text.Margin = new Thickness(0, 0, 0, 4);
        var box = new ComboBox { ItemsSource = choices, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed(box.SelectedIndex); };
        panel.Children.Add(text);
        panel.Children.Add(box);
        return panel;
    }
}
