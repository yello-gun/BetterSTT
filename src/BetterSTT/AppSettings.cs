using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Whisper.net.Ggml;

namespace BetterSTT;

public enum OutputMethod { Paste, Type }

/// <summary>Toggle: press to start, press again to stop. Hold: record while the shortcut is held down.</summary>
public enum ActivationMode { Toggle, Hold }

public enum OverlayPosition { BottomCenter, BottomLeft, BottomRight, TopCenter, TopLeft, TopRight }

/// <summary>A word fix: whenever <see cref="From"/> is heard as a whole word or phrase, type <see cref="To"/>.</summary>
public sealed class Replacement
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>Settings that apply only while dictating into one app. Null means "same as everywhere else".</summary>
public sealed class AppProfile
{
    public string ProcessName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool? AddTrailingSpace { get; set; }
    public OutputMethod? OutputMethod { get; set; }
    public bool? ShowOverlay { get; set; }
    /// <summary>Type exactly what was heard: no cleanup and no word fixes.</summary>
    public bool ExactWords { get; set; }
}

public static class AppPaths
{
    public static string Data { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterSTT");
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string Models => Path.Combine(Data, "models");
    public static string LogFile => Path.Combine(Data, "log.txt");
    /// <summary>Recordings not yet transcribed; see <see cref="PendingAudio"/>.</summary>
    public static string Pending => Path.Combine(Data, "pending");

    /// <summary>Earlier names of the app, newest first: BetterTTS (2.1), CleanDictate (1.0–2.0).</summary>
    static readonly string[] OldNames = ["BetterTTS", "CleanDictate"];

    /// <summary>
    /// Moves settings, history and models from folders used under earlier names into the BetterSTT
    /// folder, and carries over the start-with-Windows entry.
    /// </summary>
    public static void MigrateFromOldNames()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string name in OldNames)
        {
            string old = Path.Combine(localAppData, name);
            try
            {
                if (!Directory.Exists(old)) continue;
                if (!Directory.Exists(Data))
                {
                    Directory.Move(old, Data);
                    continue;
                }

                // Both exist: keep whichever copy of each file is newer. Models are identical
                // downloads, so an existing one is kept rather than moving gigabytes.
                foreach (var file in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(Data, Path.GetRelativePath(old, file));
                    if (File.Exists(target))
                    {
                        bool isModel = target.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                        if (isModel || File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(file)) continue;
                        File.Delete(target);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target);
                }
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Write($"Could not move the old {name} data folder: {ex.Message}");
            }
        }

        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run == null) return;
            bool hadStartup = false;
            foreach (string name in OldNames)
            {
                if (run.GetValue(name) == null) continue;
                hadStartup = true;
                run.DeleteValue(name, throwOnMissingValue: false);
            }
            if (hadStartup && !StartupRegistration.IsEnabled()) StartupRegistration.Apply(true);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not migrate the startup entry: {ex.Message}");
        }
    }
}

public sealed class HotkeyBinding
{
    public Keys Key { get; set; } = Keys.Space;
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>The individual key names, e.g. ["Ctrl", "Alt", "Space"].</summary>
    public IReadOnlyList<string> Parts()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key switch
        {
            >= Keys.D0 and <= Keys.D9 => ((int)Key - (int)Keys.D0).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + ((int)Key - (int)Keys.NumPad0),
            Keys.Oemtilde => "`",
            Keys.OemMinus => "-",
            Keys.Oemplus => "=",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.OemSemicolon => ";",
            Keys.OemQuotes => "'",
            Keys.Oemcomma => ",",
            Keys.OemPeriod => ".",
            Keys.OemQuestion => "/",
            Keys.OemPipe => "\\",
            Keys.Return => "Enter",
            Keys.Back => "Backspace",
            Keys.Next => "Page Down",
            Keys.Prior => "Page Up",
            _ => Key.ToString(),
        });
        return parts;
    }

    public override string ToString() => string.Join(" + ", Parts());

    public bool SameAs(HotkeyBinding other) =>
        Key == other.Key && Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift && Win == other.Win;
}

public sealed class CleanupOptions
{
    public bool Enabled { get; set; } = true;
    public bool RemoveFillerSounds { get; set; } = true;
    public bool RemoveEllipses { get; set; } = true;
    public bool RemoveStutters { get; set; } = true;
    public bool RemoveDiscourseMarkers { get; set; } = true;
    public bool RemoveNonSpeechTags { get; set; } = true;

    public List<string> FillerSounds { get; set; } =
        ["um", "uh", "uhm", "erm", "er", "hmm", "hm", "mm", "ah", "eh"];

    public List<string> DiscourseMarkers { get; set; } =
        ["like", "you know", "I mean", "basically", "literally", "actually", "you see", "well", "so", "kind of", "sort of"];

    public List<string> StutterExceptions { get; set; } = ["had", "that", "is", "bye"];
}

public sealed record ModelOption(
    string Name, string Description, string Size, int Accuracy, int Speed, bool Recommended,
    GgmlType Type, QuantizationType Quantization);

public sealed class AppSettings
{
    public static readonly ModelOption[] Models =
    [
        new("Large v3 Turbo", "Best accuracy at high speed. Great with a GPU.", "547 MB", 5, 4, true,
            GgmlType.LargeV3Turbo, QuantizationType.Q5_0),
        new("Large v3 Turbo, full precision", "Tiny accuracy gain, three times the size.", "1.6 GB", 5, 3, false,
            GgmlType.LargeV3Turbo, QuantizationType.NoQuantization),
        new("Medium (English)", "Very good English accuracy.", "515 MB", 4, 3, false,
            GgmlType.MediumEn, QuantizationType.Q5_0),
        new("Small (English)", "Good for slower PCs without a GPU.", "180 MB", 3, 4, false,
            GgmlType.SmallEn, QuantizationType.Q5_0),
        new("Base (English)", "Fastest, least accurate.", "57 MB", 2, 5, false,
            GgmlType.BaseEn, QuantizationType.Q5_0),
    ];

    /// <summary>Whisper imitates the style of its prompt, so a clean, filler-free sentence nudges it the same way.</summary>
    public const string StylePrompt =
        "Sure. I think we should start with the basics, then move on to testing the new features.";

    public static readonly (string Code, string Name)[] Languages =
    [
        ("en", "English"), ("auto", "Detect automatically"), ("es", "Spanish"), ("fr", "French"),
        ("de", "German"), ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("ja", "Japanese"), ("zh", "Chinese"),
    ];

    public HotkeyBinding Hotkey { get; set; } = new();
    public ActivationMode Activation { get; set; } = ActivationMode.Toggle;
    /// <summary>Keeps recording this long after you stop, so the last word isn't clipped.</summary>
    public int TailCaptureMs { get; set; } = 300;
    public bool PasteLastEnabled { get; set; } = true;
    // Not Ctrl+Alt+V: that is Paste Special in Office.
    public HotkeyBinding PasteLastHotkey { get; set; } = new() { Key = Keys.V, Ctrl = true, Alt = true, Shift = true };
    /// <summary>Frees the model's memory after this many idle minutes; 0 keeps it loaded.</summary>
    public int UnloadModelAfterMinutes { get; set; }
    public int MicrophoneDevice { get; set; } = -1; // -1 = Windows default
    public OutputMethod OutputMethod { get; set; } = OutputMethod.Paste;
    public bool RestoreClipboard { get; set; } = true;
    public bool AddTrailingSpace { get; set; } = true;
    public bool PlaySounds { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public int MaxRecordingMinutes { get; set; } = 10;

    public GgmlType ModelType { get; set; } = GgmlType.LargeV3Turbo;
    public QuantizationType ModelQuantization { get; set; } = QuantizationType.Q5_0;
    public bool UseGpu { get; set; } = true;
    public string Language { get; set; } = "en";
    /// <summary>Names and jargon, comma-separated, so Whisper spells them right.</summary>
    public string Vocabulary { get; set; } = "";

    /// <summary>Word fixes applied after cleanup, e.g. "git hub" → "GitHub".</summary>
    public List<Replacement> Replacements { get; set; } = new();

    /// <summary>Per-app overrides, matched by process name. Terminals get no trailing space by default.</summary>
    public List<AppProfile> AppProfiles { get; set; } =
    [
        new() { ProcessName = "WindowsTerminal", DisplayName = "Windows Terminal", AddTrailingSpace = false },
    ];

    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.BottomCenter;
    /// <summary>True: always the main screen. False: the screen the mouse is on.</summary>
    public bool OverlayOnPrimaryScreen { get; set; }

    public string BuildPrompt()
    {
        // Fixed words double as recognition hints, so Whisper hears them right more often.
        var terms = Vocabulary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(Replacements.Select(r => r.To.Trim()))
            .Select(t => t.TrimEnd('.'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return terms.Count == 0 ? StylePrompt : $"{StylePrompt} Names and terms: {string.Join(", ", terms)}.";
    }

    public CleanupOptions Cleanup { get; set; } = new();

    /// <summary>Default settings plus a few example fixes and app rules, for screenshots.</summary>
    public static AppSettings Sample()
    {
        var s = new AppSettings();
        s.Replacements.AddRange(
        [
            new() { From = "git hub", To = "GitHub" },
            new() { From = "cloud code", To = "Claude Code" },
            new() { From = "kuber netties", To = "Kubernetes" },
        ]);
        s.AppProfiles.Add(new AppProfile { ProcessName = "Code", DisplayName = "Visual Studio Code", ExactWords = true, AddTrailingSpace = false });
        return s;
    }

    public AppProfile? ProfileFor(string? processName) =>
        processName == null ? null
            : AppProfiles.FirstOrDefault(p => string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>These settings with an app's profile applied on top; unchanged when the app has none.</summary>
    public AppSettings ForApp(string? processName)
    {
        var s = Clone();
        if (s.ProfileFor(processName) is not { } p) return s;
        if (p.AddTrailingSpace is { } space) s.AddTrailingSpace = space;
        if (p.OutputMethod is { } method) s.OutputMethod = method;
        if (p.ShowOverlay is { } overlay) s.ShowOverlay = overlay;
        if (p.ExactWords) s.Cleanup.Enabled = false;
        return s;
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"Settings could not be read, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Data);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;
}

public static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Data);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 1_000_000) file.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
