using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BetterTTS.UI;

/// <summary>
/// Development aid: BetterTTS.exe --screenshots &lt;folder&gt; renders every page in light and dark
/// plus the overlay states to PNG files, then exits.
/// </summary>
static class Screenshots
{
    public static async Task RunAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        try
        {
            App.Controller.Start();
            for (int i = 0; i < 160 && App.Controller.ModelState != ModelState.Ready; i++) await Task.Delay(250);

            (string Name, Type Page)[] pages =
            [
                ("home", typeof(HomePage)), ("cleanup", typeof(CleanupPage)),
                ("speech", typeof(SpeechPage)), ("general", typeof(GeneralPage)),
            ];

            foreach (bool dark in new[] { false, true })
            {
                ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, updateAccent: false);
                Theme.ApplyAccent();
                var window = new MainWindow
                {
                    WindowBackdropType = WindowBackdropType.None,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 0,
                    Top = 0,
                    Height = 1000,
                    ShowActivated = false,
                };
                window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
                window.Show();
                await Task.Delay(1200);
                foreach (var (name, page) in pages)
                {
                    window.NavigateTo(page);
                    await Task.Delay(900);
                    Save(window, Path.Combine(dir, $"{name}-{(dark ? "dark" : "light")}.png"));
                }
                window.Close();
            }

            foreach (bool dark in new[] { false, true })
                foreach (string state in new[] { "listening", "busy", "done" })
                {
                    var overlay = new OverlayWindow(App.Controller);
                    overlay.Preview(state, dark);
                    await Task.Delay(300);
                    Save(overlay, Path.Combine(dir, $"overlay-{state}-{(dark ? "dark" : "light")}.png"));
                    overlay.Close();
                }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
        }
        App.Controller.Dispose();
        Application.Current.Shutdown();
    }

    static void Save(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
