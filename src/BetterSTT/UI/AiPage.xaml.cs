using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BetterSTT.UI;

/// <summary>Local AI settings: on/off, Ollama status, the model, timing and a place to try it.</summary>
public partial class AiPage : Page
{
    readonly DictationController _c = App.Controller;
    bool _building;
    IReadOnlyList<LocalAi.InstalledModel> _installed = [];
    bool _running;
    readonly Dictionary<string, double> _downloading = new(StringComparer.OrdinalIgnoreCase);

    const string TrySample =
        "So basically I wanted to let you know that the the report is done, I think, and it's in the shared folder. " +
        "Can you check it before Friday because I'm not sure the numbers in the second part make sense or whatever.";

    static readonly int[] TimeoutOptions = [5, 10, 20, 30, 60];

    public AiPage()
    {
        InitializeComponent();
        TryBox.Text = TrySample;
        foreach (int s in TimeoutOptions) TimeoutBox.Items.Add($"{s} seconds");
        TimeoutBox.SelectionChanged += (_, _) =>
        {
            if (!_building && TimeoutBox.SelectedIndex >= 0) _c.Update(s => s.AiTimeoutSeconds = TimeoutOptions[TimeoutBox.SelectedIndex]);
        };
        AiSwitch.Checked += (_, _) => { if (!_building) _ = TurnOnAsync(); };
        AiSwitch.Unchecked += (_, _) =>
        {
            if (_building) return;
            _c.Update(s => s.AiEnabled = false);
            Details.Visibility = Visibility.Collapsed;
        };
        Loaded += (_, _) =>
        {
            _building = true;
            AiSwitch.IsChecked = _c.Settings.AiEnabled;
            TimeoutBox.SelectedIndex = Array.IndexOf(TimeoutOptions, TimeoutOptions.MinBy(t => Math.Abs(t - _c.Settings.AiTimeoutSeconds)));
            _building = false;
            Details.Visibility = _c.Settings.AiEnabled ? Visibility.Visible : Visibility.Collapsed;
            RewriteHint.Text = $"Select text in any app, press {_c.Settings.RewriteHotkey}, say how to change it (“make it more formal”, “shorten it”, “translate it to Spanish”), then press it again. With nothing selected, say what to write.";
            if (_c.Settings.AiEnabled) _ = RefreshAsync();
        };
    }

    async Task TurnOnAsync()
    {
        _c.Update(s => s.AiEnabled = true);
        Details.Visibility = Visibility.Visible;
        StatusText.Text = "Starting Ollama…";
        await LocalAi.StartAsync();
        await RefreshAsync();
        // With exactly one model installed there's nothing to choose.
        if (string.IsNullOrWhiteSpace(_c.Settings.AiModel) && _installed.Count == 1)
        {
            _c.Update(s => s.AiModel = _installed[0].Name);
            BuildModels();
        }
    }

    async Task RefreshAsync()
    {
        _running = await LocalAi.IsRunningAsync();
        bool installed = LocalAi.FindOllama() != null;
        try { _installed = _running ? await LocalAi.ListModelsAsync() : []; }
        catch { _installed = []; }

        StatusDot.SetResourceReference(Shape.FillProperty, _running ? Theme.Success : Theme.Caution);
        (StatusText.Text, StatusHint.Text) = (_running, installed) switch
        {
            (true, _) => ("Ollama is running", $"{(_installed.Count == 1 ? "1 model" : $"{_installed.Count} models")} installed. BetterSTT only talks to it on this PC ({LocalAi.Endpoint})."),
            (false, true) => ("Ollama is installed but not running", "BetterSTT starts it when it's needed, or you can start it now."),
            _ => ("Ollama isn't installed", "Ollama is a free app that runs AI models on your PC. Install it, then come back here."),
        };
        StartButton.Visibility = !_running && installed ? Visibility.Visible : Visibility.Collapsed;
        GetButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        BuildModels();
    }

    void BuildModels()
    {
        Models.Children.Clear();
        var names = new HashSet<string>(_installed.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        // Installed models first (any of them can be used), then recommended ones to download.
        foreach (var model in _installed)
        {
            var known = LocalAi.Recommended.FirstOrDefault(r => Same(r.Name, model.Name));
            Models.Children.Add(ModelRow(model.Name, known?.Label ?? model.Name,
                $"{model.Bytes / 1e9:F1} GB{(known != null ? $" · {known.Speed} · {known.Description}" : "")}", installed: true));
        }
        foreach (var r in LocalAi.Recommended.Where(r => !names.Any(n => Same(n, r.Name))))
            Models.Children.Add(ModelRow(r.Name, r.Label, $"{r.Size} · {r.Speed} · {r.Description}", installed: false));
    }

    /// <summary>"gemma3:4b" and "gemma3:4b-it-q4_K_M" count as the same model.</summary>
    static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "-", StringComparison.OrdinalIgnoreCase);

    Border ModelRow(string name, string label, string detail, bool installed)
    {
        bool selected = string.Equals(_c.Settings.AiModel, name, StringComparison.OrdinalIgnoreCase);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Theme.Text(label, 14, weight: FontWeights.SemiBold));
        var caption = Theme.Text(detail, 12, Theme.TextSecondary);
        caption.Margin = new Thickness(0, 2, 16, 0);
        texts.Children.Add(caption);

        FrameworkElement action;
        if (installed)
        {
            var radio = new RadioButton { IsChecked = selected, GroupName = "model", VerticalAlignment = VerticalAlignment.Center, Content = selected ? "In use" : "Use" };
            System.Windows.Automation.AutomationProperties.SetName(radio, $"Use {label}");
            radio.Checked += (_, _) =>
            {
                _c.Update(s => s.AiModel = name);
                BuildModels();
            };
            action = radio;
        }
        else if (_downloading.TryGetValue(name, out double progress))
        {
            action = Theme.Text($"Downloading… {progress:P0}", 13, Theme.TextSecondary);
        }
        else
        {
            var download = new Button { Content = "Download", Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowDownload24 }, IsEnabled = _running };
            System.Windows.Automation.AutomationProperties.SetName(download, $"Download {label}");
            download.Click += (_, _) => _ = DownloadAsync(name);
            action = download;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(texts);
        Grid.SetColumn(action, 1);
        action.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(action);
        var card = new Border { Child = grid, Style = (Style)FindResource("Card") };
        if (selected) card.Res(Border.BorderBrushProperty, Theme.Accent);
        return card;
    }

    async Task DownloadAsync(string name)
    {
        _downloading[name] = 0;
        BuildModels();
        var progress = new Progress<double>(p =>
        {
            // Redraw only on whole-percent steps.
            if ((int)(p * 100) == (int)(_downloading.GetValueOrDefault(name) * 100)) return;
            _downloading[name] = p;
            BuildModels();
        });
        try
        {
            await Task.Run(() => LocalAi.PullAsync(name, progress, CancellationToken.None));
            if (string.IsNullOrWhiteSpace(_c.Settings.AiModel)) _c.Update(s => s.AiModel = name);
            _c.Announce($"{name} is downloaded and ready.");
        }
        catch (Exception ex)
        {
            _c.Announce($"Couldn't download {name}: {ex.Message}");
        }
        finally
        {
            _downloading.Remove(name);
            await RefreshAsync();
        }
    }

    void OnDownloadOther(object sender, RoutedEventArgs e)
    {
        string name = OtherBox.Text.Trim();
        if (!LocalAi.IsValidModelName(name))
        {
            OtherHint.Text = "That doesn't look like an Ollama model name. Names look like gemma3:4b or llama3.3.";
            return;
        }
        if (!_running)
        {
            OtherHint.Text = "Start Ollama first (above).";
            return;
        }
        OtherHint.Text = $"Downloading {name}… it appears in the list above when it's ready.";
        OtherBox.Text = "";
        _ = DownloadAsync(name);
    }

    async void OnTry(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_c.Settings.AiModel))
        {
            TryStatus.Text = "Choose a model above first.";
            return;
        }
        TryButton.IsEnabled = false;
        TryResult.Text = "";
        TryStatus.Text = "Working… (the first run also loads the model)";
        var timer = Stopwatch.StartNew();
        try
        {
            await LocalAi.StartAsync();
            string text = TryBox.Text.Trim();
            string cleaned = TextCleaner.Process(text, _c.Settings); // the rules run first, as in a dictation
            string reply = LocalAi.Tidy(await LocalAi.ChatAsync(_c.Settings.AiModel,
                LocalAi.PolishSystemPrompt(_c.Settings.CurrentStyle.AiInstructions), LocalAi.Wrap(cleaned), CancellationToken.None));
            string? problem = LocalAi.CheckPolish(cleaned, reply);
            TryResult.Text = reply;
            TryStatus.Text = $"{timer.Elapsed.TotalSeconds:F1} s with {_c.Settings.AiModel}"
                             + (problem != null ? $" · a dictation wouldn't use this: {problem}" : "");
        }
        catch (Exception ex)
        {
            TryStatus.Text = $"Didn't work: {ex.Message}";
        }
        finally
        {
            TryButton.IsEnabled = true;
        }
    }

    async void OnStartOllama(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Starting Ollama…";
        await LocalAi.StartAsync();
        await RefreshAsync();
    }

    void OnGetOllama(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://ollama.com/download") { UseShellExecute = true });

    async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    void OnOpenStyle(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.NavigateTo(typeof(StylePage));

    void OnOpenGeneral(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.NavigateTo(typeof(GeneralPage));
}
