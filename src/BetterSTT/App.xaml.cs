using System.Windows;
using BetterSTT.UI;
using Wpf.Ui.Appearance;

namespace BetterSTT;

public partial class App : Application
{
    readonly AppSettings _settings;
    readonly bool _background;
    readonly EventWaitHandle _showSignal;
    readonly string? _screenshotDir;

    DictationController? _controller;
    TrayIcon? _tray;
    OverlayWindow? _overlay;
    MainWindow? _main;
    volatile bool _exiting;

    public App(AppSettings settings, bool background, EventWaitHandle showSignal, string? screenshotDir)
    {
        _settings = settings;
        _background = background;
        _showSignal = showSignal;
        _screenshotDir = screenshotDir;
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"UI error: {e.Exception}");
            e.Handled = true;
        };
    }

    public static DictationController Controller => ((App)Current)._controller!;

    /// <summary>The on-screen pill, for the position preview in settings (null in screenshot mode).</summary>
    public static OverlayWindow? Overlay => ((App)Current)._overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.ApplySystemTheme();
        // When Windows switches light/dark, the theme watcher swaps the palette; put our accent back on top.
        ApplicationThemeManager.Changed += (_, _) => Dispatcher.BeginInvoke(Theme.ApplyAccent);
        _controller = new DictationController(_settings, _screenshotDir != null ? HistoryStore.Sample() : null);

        if (_screenshotDir != null)
        {
            _ = Screenshots.RunAsync(_screenshotDir);
            return;
        }

        _overlay = new OverlayWindow(_controller);
        _tray = new TrayIcon(_controller, ShowMainWindow, ExitApp);
        _controller.Start();
        ListenForShowSignal();
        if (!_background) ShowMainWindow();
    }

    /// <summary>Another launch of BetterSTT signals this instance to open its window.</summary>
    void ListenForShowSignal()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                _showSignal.WaitOne();
                if (_exiting) return;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        })
        { IsBackground = true, Name = "ShowSignal" };
        thread.Start();
    }

    public void ShowMainWindow()
    {
        if (_main == null)
        {
            Theme.ApplySystemTheme();
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Show();
        _main.Activate();
    }

    public void ExitApp()
    {
        _exiting = true;
        _main?.Close();
        _overlay?.Close();
        _tray?.Dispose();
        _controller?.Dispose();
        Shutdown();
    }

    public void RestartApp()
    {
        Program.Restart();
        ExitApp();
    }
}
