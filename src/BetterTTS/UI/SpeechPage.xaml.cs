using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;

namespace BetterTTS.UI;

public partial class SpeechPage : Page
{
    readonly DictationController _c = App.Controller;
    readonly DispatcherTimer _vocabSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _building;

    sealed record LanguageItem(string Code, string Name);

    public SpeechPage()
    {
        InitializeComponent();

        GpuSwitch.Checked += (_, _) => SetGpu(true);
        GpuSwitch.Unchecked += (_, _) => SetGpu(false);

        LanguageBox.ItemsSource = AppSettings.Languages.Select(l => new LanguageItem(l.Code, l.Name)).ToList();
        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (!_building && LanguageBox.SelectedItem is LanguageItem l) _c.Update(s => s.Language = l.Code);
        };

        _vocabSave.Tick += (_, _) => { _vocabSave.Stop(); SaveVocabulary(); };
        VocabularyBox.TextChanged += (_, _) => { if (!_building) { _vocabSave.Stop(); _vocabSave.Start(); } };
        VocabularyBox.LostFocus += (_, _) => SaveVocabulary();

        Loaded += (_, _) =>
        {
            _c.ModelChanged += Refresh;
            _c.DictationFinished += OnFinished;
            _c.SettingsChanged += RefreshEngine;
            Build();
        };
        Unloaded += (_, _) =>
        {
            _c.ModelChanged -= Refresh;
            _c.DictationFinished -= OnFinished;
            _c.SettingsChanged -= RefreshEngine;
            if (_vocabSave.IsEnabled) SaveVocabulary();
        };
    }

    void Build()
    {
        _building = true;
        GpuSwitch.IsChecked = _c.Settings.UseGpu;
        var languages = (List<LanguageItem>)LanguageBox.ItemsSource;
        LanguageBox.SelectedItem = languages.FirstOrDefault(l => l.Code == _c.Settings.Language)
                                   ?? new LanguageItem(_c.Settings.Language, _c.Settings.Language);
        VocabularyBox.Text = _c.Settings.Vocabulary;
        _building = false;
        Refresh();
    }

    void Refresh()
    {
        RefreshEngine();
        RefreshModels();
    }

    void OnFinished(DictationOutcome _) => RefreshEngine();

    void RefreshEngine()
    {
        bool gpu = _c.RuntimeName.Contains("Vulkan") || _c.RuntimeName.Contains("Cuda");
        switch (_c.ModelState)
        {
            case ModelState.Ready:
                EngineTitle.Text = gpu ? "Running on your GPU" : "Running on your CPU";
                string device = gpu ? $"{SystemInfo.GpuName ?? "GPU"} · {_c.RuntimeName}" : "Processor";
                EngineDetail.Text = _c.LastAudioSeconds > 0
                    ? $"{device} · last dictation: {_c.LastAudioSeconds:F1} s of speech in {_c.LastTranscribeSeconds:F2} s"
                    : device;
                break;
            case ModelState.Downloading:
                EngineTitle.Text = "Downloading the speech model…";
                EngineDetail.Text = $"{_c.DownloadedMb} MB so far. This happens once per model.";
                break;
            case ModelState.Failed:
                EngineTitle.Text = "The speech model didn't load";
                EngineDetail.Text = _c.ModelError ?? "";
                break;
            default:
                EngineTitle.Text = "Loading the speech model…";
                EngineDetail.Text = "This takes a few seconds.";
                break;
        }

        string tone = _c.ModelState switch
        {
            ModelState.Ready => gpu ? Theme.Success : Theme.TextSecondary,
            ModelState.Failed => Theme.Critical,
            _ => Theme.Caution,
        };
        EngineIcon.SetResourceReference(ForegroundProperty, tone);
        EngineTile.SetResourceReference(Border.BackgroundProperty, _c.ModelState == ModelState.Ready && gpu ? Theme.SuccessBackground : Theme.SubtleFill);
        RestartButton.Visibility = _c.RestartNeeded ? Visibility.Visible : Visibility.Collapsed;
    }

    void RefreshModels()
    {
        ModelList.Children.Clear();
        foreach (var model in AppSettings.Models) ModelList.Children.Add(ModelCard(model));
    }

    Button ModelCard(ModelOption m)
    {
        bool selected = m.Type == _c.Settings.ModelType && m.Quantization == _c.Settings.ModelQuantization;
        bool downloaded = Transcriber.IsDownloaded(m);

        // Radio indicator
        var radio = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var outer = new Ellipse { StrokeThickness = selected ? 6 : 1 };
        outer.SetResourceReference(Shape.StrokeProperty, selected ? Theme.Accent : Theme.StrongStroke);
        radio.Children.Add(outer);

        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(Theme.Text(m.Name));
        if (m.Recommended)
        {
            var badge = Theme.Pill("Recommended", Theme.SubtleFill, "AccentTextFillColorPrimaryBrush");
            badge.Margin = new Thickness(10, 0, 0, 0);
            name.Children.Add(badge);
        }
        var texts = new StackPanel { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(name);
        var desc = Theme.Text(m.Description, 12, Theme.TextSecondary);
        desc.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(desc);

        var ratings = new StackPanel { Width = 160, VerticalAlignment = VerticalAlignment.Center };
        ratings.Children.Add(Rating("Accuracy", m.Accuracy));
        ratings.Children.Add(Rating("Speed", m.Speed));

        string statusText;
        string statusBg, statusFg;
        if (selected && _c.ModelState == ModelState.Downloading)
            (statusText, statusBg, statusFg) = ($"Downloading {_c.DownloadedMb} MB", Theme.SubtleFill, Theme.Caution);
        else if (selected && _c.ModelState == ModelState.Loading)
            (statusText, statusBg, statusFg) = ("Loading…",Theme.SubtleFill, Theme.TextSecondary);
        else if (selected && _c.ModelState == ModelState.Ready)
            (statusText, statusBg, statusFg) = ("In use", Theme.SuccessBackground, Theme.TextPrimary);
        else if (downloaded)
            (statusText, statusBg, statusFg) = ("Downloaded", Theme.SubtleFill, Theme.TextSecondary);
        else
            (statusText, statusBg, statusFg) = ("Not downloaded", Theme.SubtleFill, Theme.TextSecondary);

        var right = new StackPanel { Width = 150, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var size = Theme.Text(m.Size, 12, Theme.TextSecondary);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(size);
        var status = Theme.Pill(statusText, statusBg, statusFg);
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.Margin = new Thickness(0, 4, 0, 0);
        right.Children.Add(status);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(radio);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(ratings, 2);
        Grid.SetColumn(right, 3);
        grid.Children.Add(texts);
        grid.Children.Add(ratings);
        grid.Children.Add(right);

        var card = new Button
        {
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(20, 14, 20, 14),
            Margin = new Thickness(0, 0, 0, 4),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = downloaded ? null : $"Selecting this downloads {m.Size}.",
        };
        card.Res(Control.BackgroundProperty, Theme.CardFill).Res(Control.BorderBrushProperty, selected ? Theme.Accent : Theme.CardStroke);
        System.Windows.Automation.AutomationProperties.SetName(card, $"{m.Name}, {statusText}");
        card.Click += (_, _) =>
        {
            if (selected) return;
            _c.Update(s => { s.ModelType = m.Type; s.ModelQuantization = m.Quantization; });
            RefreshModels();
        };
        return card;
    }

    static StackPanel Rating(string label, int value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var text = Theme.Text(label, 12, Theme.TextSecondary);
        text.Width = 62;
        row.Children.Add(text);
        for (int i = 0; i < 5; i++)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.FillProperty, i < value ? Theme.Accent : Theme.ControlStroke);
            if (i >= value) dot.SetResourceReference(Shape.StrokeProperty, Theme.StrongStroke);
            dot.StrokeThickness = i < value ? 0 : 0.5;
            row.Children.Add(dot);
        }
        return row;
    }

    void SetGpu(bool on)
    {
        if (_building) return;
        _c.Update(s => s.UseGpu = on);
    }

    void SaveVocabulary()
    {
        _vocabSave.Stop();
        string text = VocabularyBox.Text.Trim();
        if (text != _c.Settings.Vocabulary) _c.Update(s => s.Vocabulary = text);
    }

    void OnRestart(object sender, RoutedEventArgs e) => ((App)Application.Current).RestartApp();
}
