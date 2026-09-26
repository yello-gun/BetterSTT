using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace BetterSTT;

/// <summary>Tray icons drawn in code: a colored disc with a white microphone.</summary>
public static class TrayIcons
{
    public static readonly Icon Idle = Make(Color.FromArgb(0x4B, 0x8B, 0xF5));
    public static readonly Icon Recording = Make(Color.FromArgb(0xE5, 0x48, 0x4D));
    public static readonly Icon Busy = Make(Color.FromArgb(0xF0, 0xA0, 0x20));
    public static readonly Icon Loading = Make(Color.FromArgb(0x8A, 0x8F, 0x98));

    static Icon Make(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(color)) g.FillEllipse(fill, 1, 1, 30, 30);
            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using (var capsule = new GraphicsPath())
            {
                capsule.AddArc(12.5f, 6, 7, 7, 180, 180);
                capsule.AddArc(12.5f, 12, 7, 7, 0, 180);
                capsule.CloseFigure();
                g.FillPath(white, capsule);
            }
            g.DrawArc(pen, 9, 9.5f, 14, 13, 0, 180);
            g.DrawLine(pen, 16, 22.5f, 16, 26);
        }
        IntPtr handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Short synthesized cues so there are no audio files to ship.</summary>
public static class Sounds
{
    static readonly SoundPlayer StartPlayer = new(Tone([660, 990], 70));
    static readonly SoundPlayer StopPlayer = new(Tone([990, 660], 70));
    static readonly SoundPlayer ErrorPlayer = new(Tone([220, 220], 110));

    public static bool Enabled { get; set; } = true;

    public static void Start() => Play(StartPlayer);
    public static void Stop() => Play(StopPlayer);
    public static void Error() => Play(ErrorPlayer);

    static void Play(SoundPlayer p)
    {
        if (!Enabled) return;
        try { p.Play(); } catch { /* no audio device */ }
    }

    static MemoryStream Tone(int[] freqs, int msEach)
    {
        const int rate = 22050;
        int per = rate * msEach / 1000;
        var samples = new short[per * freqs.Length];
        for (int t = 0; t < freqs.Length; t++)
            for (int i = 0; i < per; i++)
            {
                double env = Math.Min(1, Math.Min(i, per - i) / (rate * 0.008));
                samples[t * per + i] = (short)(Math.Sin(2 * Math.PI * freqs[t] * i / rate) * env * 6000);
            }

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(dataBytes);
            foreach (var s in samples) w.Write(s);
        }
        ms.Position = 0;
        return ms;
    }
}

/// <summary>The notification-area icon and its menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly DictationController _controller;
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripMenuItem _toggle, _copyLast;

    public TrayIcon(DictationController controller, Action openWindow, Action exit)
    {
        _controller = controller;
        _toggle = new WinForms.ToolStripMenuItem("", null, (_, _) => controller.Toggle());
        _copyLast = new WinForms.ToolStripMenuItem("Copy last dictation", null, (_, _) =>
        {
            var last = controller.History.Recent.FirstOrDefault();
            if (last != null) TextInjector.SetClipboard(last.Clean);
        });
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            new WinForms.ToolStripMenuItem("Open BetterSTT", null, (_, _) => openWindow()) { Font = new Font(WinForms.Control.DefaultFont, FontStyle.Bold) },
            _toggle,
            _copyLast,
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Exit", null, (_, _) => exit()),
        ]);
        menu.Opening += (_, _) => Refresh();

        _icon = new WinForms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) openWindow(); };

        controller.StateChanged += Refresh;
        controller.ModelChanged += Refresh;
        controller.SettingsChanged += Refresh;
        controller.Notice += (message, isError) =>
            _icon.ShowBalloonTip(5000, "BetterSTT", message, isError ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
        Refresh();
    }

    void Refresh()
    {
        var c = _controller;
        _icon.Icon = c.State switch
        {
            DictationState.Recording => TrayIcons.Recording,
            DictationState.Transcribing => TrayIcons.Busy,
            _ => c.ModelState == ModelState.Ready ? TrayIcons.Idle : TrayIcons.Loading,
        };
        string text = $"BetterSTT — {c.StatusText} ({c.Settings.Hotkey})";
        _icon.Text = text.Length <= 127 ? text : text[..127];
        _toggle.Text = (c.State == DictationState.Recording ? "Stop dictation" : "Start dictation") + $"\t{c.Settings.Hotkey}";
        _toggle.Enabled = c.State != DictationState.Transcribing;
        _copyLast.Enabled = c.History.Recent.Count > 0;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
