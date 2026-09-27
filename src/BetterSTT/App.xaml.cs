using System.Windows;
using BetterSTT.UI;
using Wpf.Ui.Appearance;

namespace BetterSTT;

public partial class App : Application
{
    readonly AppSettings _settings;
    readonly bool _background;
    readonly Signals _signals;
    readonly string? _screenshotDir;
    readonly string? _command;
    readonly bool _updated;

    DictationController? _controller;
    TrayIcon? _tray;
    OverlayWindow? _overlay;
    MainWindow? _main;
    volatile bool _exiting;

    public App(AppSettings settings, bool background, Signals signals, string? screenshotDir, string? command, bool updated)
    {
        _settings = settings;
        _background = background;
        _signals = signals;
        _screenshotDir = screenshotDir;
        _command = command;
        _updated = updated;
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
        _controller.Start(checkForUpdates: !TextInjector.ClipboardOnly);
        ListenForSignals();
        if (!_background) ShowMainWindow();
        if (_updated) _controller.Announce($"BetterSTT was updated to {Updater.Current.ToString(3)}.");
        if (_command != null) RunCommand(_command);
    }

    /// <summary>Another launch of BetterSTT asks this instance to open its window or run a command.</summary>
    void ListenForSignals()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                string signal = _signals.Wait();
                if (_exiting) return;
                Dispatcher.BeginInvoke(() => RunCommand(signal));
            }
        })
        { IsBackground = true, Name = "Signals" };
        thread.Start();
    }

    void RunCommand(string command)
    {
        switch (command)
        {
            case Signals.Show:
                // Opening the app is when a downloaded update installs, unless it's busy.
                if (_controller!.Settings.CheckForUpdates && _controller.AvailableUpdate is { } update
                    && _controller.State == DictationState.Idle && Updater.ShouldAutoInstall(update))
                {
                    InstallUpdate();
                    return;
                }
                ShowMainWindow();
                break;
            case "toggle": _controller!.Toggle(); break;
            case "paste-last": _ = _controller!.PasteLastAsync(); break;
            case "cancel": _ = _controller!.CancelAsync(); break;
        }
    }

    /// <summary>Installs the downloaded update and reopens BetterSTT with its window. False if it couldn't start.</summary>
    public bool InstallUpdate()
    {
        if (_controller?.AvailableUpdate is not { } update || _controller.State != DictationState.Idle) return false;
        if (!Updater.Install(update, openWindow: true)) return false;
        ExitApp();
        return true;
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
