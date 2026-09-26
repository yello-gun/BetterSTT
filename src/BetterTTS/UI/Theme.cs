using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterTTS.UI;

/// <summary>
/// Helpers for UI built in code. Colors are attached as resource references so they follow
/// light/dark switches live, exactly like DynamicResource in XAML.
/// </summary>
static class Theme
{
    /// <summary>BetterTTS's own accent, used instead of the Windows accent so controls always read clearly.</summary>
    static readonly Color BrandAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>Follows Windows light/dark, keeping the BetterTTS accent.</summary>
    public static void ApplySystemTheme()
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme(updateAccent: false);
        ApplyAccent();
    }

    public static void ApplyAccent() =>
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(BrandAccent, Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme());

    public const string TextPrimary = "TextFillColorPrimaryBrush";
    public const string TextSecondary = "TextFillColorSecondaryBrush";
    public const string Accent = "AccentFillColorDefaultBrush";
    public const string OnAccent = "TextOnAccentFillColorPrimaryBrush";
    public const string Critical = "SystemFillColorCriticalBrush";
    public const string CriticalBackground = "SystemFillColorCriticalBackgroundBrush";
    public const string Success = "SystemFillColorSuccessBrush";
    public const string SuccessBackground = "SystemFillColorSuccessBackgroundBrush";
    public const string Caution = "SystemFillColorCautionBrush";
    public const string ControlFill = "ControlFillColorDefaultBrush";
    public const string ControlStroke = "ControlStrokeColorDefaultBrush";
    public const string SubtleFill = "SubtleFillColorSecondaryBrush";
    public const string CardFill = "CardBackgroundFillColorDefaultBrush";
    public const string CardStroke = "CardStrokeColorDefaultBrush";
    public const string Divider = "DividerStrokeColorDefaultBrush";
    public const string StrongStroke = "ControlStrongStrokeColorDefaultBrush";

    /// <summary>The color that represents the controller's current status.</summary>
    public static string StatusKey(DictationController c) => c.State switch
    {
        DictationState.Recording => Critical,
        DictationState.Transcribing => Caution,
        _ => c.ModelState switch
        {
            ModelState.Ready => Success,
            ModelState.Failed => Critical,
            _ => Caution,
        },
    };

    public static T Res<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static TextBlock Text(string text, double size = 14, string brush = TextPrimary, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        return t.Res(TextBlock.ForegroundProperty, brush);
    }

    public static Border Keycap(string text, double fontSize = 12) => new Border
    {
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1, 1, 1, 2),
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, fontSize, weight: FontWeights.SemiBold),
    }.Res(Border.BackgroundProperty, ControlFill).Res(Border.BorderBrushProperty, StrongStroke);

    public static StackPanel Keycaps(HotkeyBinding binding, double fontSize = 12)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in binding.Parts()) panel.Children.Add(Keycap(part, fontSize));
        return panel;
    }

    public static Border Pill(string text, string background, string foreground) => new Border
    {
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(10, 3, 10, 3),
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 12, foreground),
    }.Res(Border.BackgroundProperty, background);

    /// <summary>Fills a TextBlock with the raw transcript, removed parts struck through in red.</summary>
    public static void ShowDiff(TextBlock target, string raw, string clean)
    {
        target.Inlines.Clear();
        foreach (var part in TextDiff.Compare(raw, clean))
        {
            var run = new Run(part.Text);
            if (part.Removed)
            {
                run.TextDecorations = TextDecorations.Strikethrough;
                run.SetResourceReference(TextElement.ForegroundProperty, Critical);
                run.SetResourceReference(TextElement.BackgroundProperty, CriticalBackground);
            }
            target.Inlines.Add(run);
        }
    }

    /// <summary>A row of equal bars used as a live audio waveform.</summary>
    public static Rectangle[] AddWaveBars(Panel host, int count, string brush)
    {
        var bars = new Rectangle[count];
        for (int i = 0; i < count; i++)
        {
            bars[i] = new Rectangle
            {
                Width = 3,
                Height = 4,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            bars[i].SetResourceReference(Shape.FillProperty, brush);
            host.Children.Add(bars[i]);
        }
        return bars;
    }

    /// <summary>Scrolls the waveform left and adds the newest level on the right.</summary>
    public static void PushLevel(Rectangle[] bars, float level, double maxHeight)
    {
        for (int i = 0; i < bars.Length - 1; i++) bars[i].Height = bars[i + 1].Height;
        bars[^1].Height = 4 + Math.Min(1.0, level * 4) * (maxHeight - 4);
    }

    public static string TimeAgo(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
        return time.ToString("MMM d, h:mm tt");
    }
}
