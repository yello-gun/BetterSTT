using System.Windows;
using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BetterSTT.UI;

public partial class MainWindow : FluentWindow
{
    readonly DictationController _controller = App.Controller;

    /// <summary>The update the banner was closed for; it stays hidden for that version until the app restarts.</summary>
    static Version? _hiddenUpdate;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType, updateAccents: false);
            Nav.Navigate(typeof(HomePage));
        };
        _controller.StateChanged += RefreshStatus;
        _controller.ModelChanged += RefreshStatus;
        _controller.UpdateChanged += RefreshUpdate;
        Closed += (_, _) =>
        {
            _controller.StateChanged -= RefreshStatus;
            _controller.ModelChanged -= RefreshStatus;
            _controller.UpdateChanged -= RefreshUpdate;
        };
        RefreshStatus();
    }

    public void NavigateTo(Type page) => Nav.Navigate(page);

    void RefreshStatus()
    {
        StatusText.Text = _controller.StatusText;
        StatusDot.SetResourceReference(Shape.FillProperty, Theme.StatusKey(_controller));
        RefreshUpdate();
    }

    void RefreshUpdate()
    {
        var update = _controller.AvailableUpdate;
        bool show = update != null && update.Version != _hiddenUpdate;
        UpdateBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (update == null) return;
        UpdateTitle.Text = $"New update: BetterSTT {update.Version.ToString(3)}";
        // Installing closes the app, so it waits until a dictation in progress is finished.
        UpdateButton.IsEnabled = _controller.State == DictationState.Idle;
    }

    void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (!((App)Application.Current).InstallUpdate())
            UpdateTitle.Text = "The update couldn't start. Try again, or download it from GitHub Releases.";
    }

    void OnHideUpdate(object sender, RoutedEventArgs e)
    {
        _hiddenUpdate = _controller.AvailableUpdate?.Version;
        RefreshUpdate();
    }
}
