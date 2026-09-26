using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BetterTTS.UI;

public partial class MainWindow : FluentWindow
{
    readonly DictationController _controller = App.Controller;

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
        Closed += (_, _) =>
        {
            _controller.StateChanged -= RefreshStatus;
            _controller.ModelChanged -= RefreshStatus;
        };
        RefreshStatus();
    }

    public void NavigateTo(Type page) => Nav.Navigate(page);

    void RefreshStatus()
    {
        StatusText.Text = _controller.StatusText;
        StatusDot.SetResourceReference(Shape.FillProperty, Theme.StatusKey(_controller));
    }
}
