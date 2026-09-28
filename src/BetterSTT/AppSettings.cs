using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Whisper.net.Ggml;

namespace BetterSTT;

public enum OutputMethod { Paste, Type }

/// <summary>
/// Toggle: press to start, press again to stop. Hold: record while the shortcut is held down.
/// HandsFree: keeps listening, with no time limit, transcribing quietly at each pause, and types
/// everything when it's turned off.
/// </summary>
public enum ActivationMode { Toggle, Hold, HandsFree }

public enum OverlayPosition { BottomCenter, BottomLeft, BottomRight, TopCenter, TopLeft, TopRight }

public enum OverlaySize { Small, Normal, Large }

/// <summary>A word fix: whenever <see cref="From"/> is heard as a whole word or phrase, type <see cref="To"/>.</summary>
public sealed class Replacement
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>Saying <see cref="Trigger"/> as a whole dictation types <see cref="Text"/> instead.</summary>
public sealed class Snippet
{
    public string Trigger { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>How a style writes spoken math: left as words, as symbols (x² + 1/2), or as LaTeX (x^2 + \frac{1}{2}).</summary>
public enum MathFormat { Off, Symbols, Latex }

/// <summary>Settings that apply only while dictating into one app. Null means "same as everywhere else".</summary>
public sealed class AppProfile
{
    public string ProcessName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool? AddTrailingSpace { get; set; }
    public OutputMethod? OutputMethod { get; set; }
    public bool? ShowOverlay { get; set; }
    /// <summary>Writing style for this app, by name; null uses the usual style.</summary>
    public string? Style { get; set; }
    /// <summary>Speech language for this app (a code from <see cref="AppSettings.Languages"/>); null uses the usual one.</summary>
    public string? Language { get; set; }
    /// <summary>
    /// Turns line breaks into spaces, e.g. for terminals, where each line of a multi-line paste can run as a
    /// command. Null keeps them.
    /// </summary>
    public bool? JoinLines { get; set; }
    /// <summary>Before 2.5: exact words per app. Read only to migrate it to <see cref="Style"/>.</summary>
    public bool ExactWords { get; set; }
}

/// <summary>
/// How cleaned-up text is written. Every style except Exact words also runs the shared cleanup rules
/// (fillers, pauses, stutters…) and word fixes.
/// </summary>
public sealed class WritingStyle
{
    public const string ExactName = "Exact words", NaturalName = "Natural", FormalName = "Formal", MathName = "Math", CodeName = "Code";

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Built-in styles can be edited but not renamed or deleted.</summary>
    public bool BuiltIn { get; set; }
    /// <summary>Type exactly what was heard: no cleanup, no word fixes, no style changes.</summary>
    public bool ExactWords { get; set; }

    /// <summary>Drops vague tails that add no information, e.g. "grab lunch or something" → "grab lunch".</summary>
    public bool RemoveVagueEndings { get; set; }
    public List<string> VagueEndings { get; set; } = DefaultVagueEndings();

    /// <summary>Splits text into paragraphs separated by a blank line.</summary>
    public bool AutoParagraphs { get; set; }
    /// <summary>Before 3.0 paragraphs broke after this many sentences; they now break where the point shifts. Unused.</summary>
    public int MaxSentencesPerParagraph { get; set; } = 4;
    /// <summary>How readily a new paragraph starts at a point shift.</summary>
    public ParagraphEagerness ParagraphEagerness { get; set; } = ParagraphEagerness.Balanced;

    /// <summary>
    /// Removes wordiness ("due to the fact that" → "because", "I just wanted to let you know that …", "really").
    /// Null means the style's default: on for Formal, off for the others.
    /// </summary>
    public bool? Concise { get; set; }
    /// <summary>Intensifiers dropped by <see cref="Concise"/> when they add nothing.</summary>
    public List<string> EmptyWords { get; set; } = Wordiness.DefaultEmptyWords();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConcise => Concise ?? (BuiltIn && Name == FormalName);
    /// <summary>"Hi John," and "Thanks," each on their own line.</summary>
    public bool GreetingAndSignOffLines { get; set; } = true;
    /// <summary>A sentence starting with one of these (a change of topic) begins a new paragraph.</summary>
    public List<string> ParagraphStarters { get; set; } = DefaultParagraphStarters();

    /// <summary>Saying "new line" or "new paragraph" starts one.</summary>
    public bool SpokenLayout { get; set; } = true;
    /// <summary>Saying "comma", "period", "question mark"… types the mark.</summary>
    public bool SpokenPunctuation { get; set; }

    /// <summary>After the rules, local AI edits the text following <see cref="AiInstructions"/> (needs AI turned on).</summary>
    public bool AiPolish { get; set; }
    public string AiInstructions { get; set; } = "";

    /// <summary>Turns spoken code ("camel case user name", "dot", "underscore") into identifiers and symbols.</summary>
    public bool SpokenCode { get; set; }

    /// <summary>Turns spoken math ("x squared plus one") into symbols or LaTeX.</summary>
    public MathFormat SpokenMath { get; set; }

    public static List<string> DefaultVagueEndings() =>
    [
        "or something", "or something like that", "or whatever", "or anything", "or anything like that",
        "and stuff", "and stuff like that", "and everything", "and all that", "and all that stuff",
        "if that makes sense", "you know what I mean",
    ];

    public static List<string> DefaultParagraphStarters() =>
    [
        "Also", "Additionally", "In addition", "Another thing", "On another note", "Separately", "Moving on",
        "Next", "Second", "Secondly", "Third", "Thirdly", "Finally", "Lastly", "However", "That said",
        "Anyway", "Besides that", "Furthermore", "Overall", "In summary", "To sum up",
    ];

    public static WritingStyle Exact() => new()
    {
        Name = ExactName, BuiltIn = true, ExactWords = true,
        Description = "Types exactly what was heard. No cleanup and no word fixes.",
    };

    public static WritingStyle Natural() => new()
    {
        Name = NaturalName, BuiltIn = true,
        Description = "Removes fillers, pauses and stutters but keeps your wording.",
    };

    public static WritingStyle Formal() => new()
    {
        Name = FormalName, BuiltIn = true, RemoveVagueEndings = true, AutoParagraphs = true,
        Description = "Also cuts wordiness and vague endings like “or something”, and starts a new paragraph where the point shifts.",
    };

    public static WritingStyle MathStyle() => new()
    {
        Name = MathName, BuiltIn = true, SpokenMath = MathFormat.Symbols,
        Description = "Writes spoken math as symbols: “x squared plus one over two” → x² + 1/2.",
    };

    public static WritingStyle CodeStyle() => new()
    {
        Name = CodeName, BuiltIn = true, SpokenCode = true,
        Description = "For code and terminals: “camel case user name” → userName, “file dot txt” → file.txt.",
    };

    public static List<WritingStyle> Defaults() => [Exact(), Natural(), Formal(), MathStyle(), CodeStyle()];
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
            Keys.XButton1 => "Mouse back button",
            Keys.XButton2 => "Mouse forward button",
            Keys.MButton => "Middle click",
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
    /// <summary>"Tuesday, no wait, Wednesday" → "Wednesday", worked out from context.</summary>
    public bool FixCorrections { get; set; } = true;

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

    /// <summary>"es" → "Spanish". Null for no code.</summary>
    public static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var known = Languages.FirstOrDefault(l => l.Code == code);
        if (known.Name != null) return known.Name;
        try { return new System.Globalization.CultureInfo(code).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }

    public HotkeyBinding Hotkey { get; set; } = new();
    public ActivationMode Activation { get; set; } = ActivationMode.Toggle;
    /// <summary>Keeps recording this long after you stop, so the last word isn't clipped.</summary>
    public int TailCaptureMs { get; set; } = 300;
    public bool PasteLastEnabled { get; set; } = true;
    // Not Ctrl+Alt+V: that is Paste Special in Office.
    public HotkeyBinding PasteLastHotkey { get; set; } = new() { Key = Keys.V, Ctrl = true, Alt = true, Shift = true };
    /// <summary>Select a word in any app and press this to add it to the dictionary.</summary>
    public bool AddWordEnabled { get; set; } = true;
    public HotkeyBinding AddWordHotkey { get; set; } = new() { Key = Keys.D, Ctrl = true, Alt = true };
    /// <summary>Local AI through Ollama. Off until the user turns it on; nothing else AI-related runs while off.</summary>
    public bool AiEnabled { get; set; }
    /// <summary>The Ollama model to use, e.g. "gemma3:4b".</summary>
    public string AiModel { get; set; } = "";
    /// <summary>How long to wait for an AI edit before typing the rule-cleaned text instead.</summary>
    public int AiTimeoutSeconds { get; set; } = 10;
    /// <summary>Select text anywhere, press this, and say how to change it.</summary>
    public bool RewriteEnabled { get; set; } = true;
    public HotkeyBinding RewriteHotkey { get; set; } = new() { Key = Keys.R, Ctrl = true, Alt = true };

    /// <summary>While on, dictations aren't kept in the recent list (only the totals count them).</summary>
    public bool PrivateMode { get; set; }
    /// <summary>Line breaks become spaces (set per app on the Apps page).</summary>
    public bool JoinLines { get; set; }
    /// <summary>Frees the model's memory after this many idle minutes; 0 keeps it loaded.</summary>
    public int UnloadModelAfterMinutes { get; set; }
    public int MicrophoneDevice { get; set; } = -1; // -1 = Windows default
    public OutputMethod OutputMethod { get; set; } = OutputMethod.Paste;
    public bool RestoreClipboard { get; set; } = true;
    public bool AddTrailingSpace { get; set; } = true;
    public bool PlaySounds { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    /// <summary>Shows a draft of your words in the indicator while you speak.</summary>
    public bool LivePreview { get; set; } = true;
    public bool StartWithWindows { get; set; }
    /// <summary>How many recent dictations Home keeps; 0 keeps them all.</summary>
    public int RecentLimit { get; set; } = 3;
    public static readonly int[] RecentLimits = [3, 5, 10, 15, 0];
    public int MaxRecordingMinutes { get; set; } = 10;

    public GgmlType ModelType { get; set; } = GgmlType.LargeV3Turbo;
    public QuantizationType ModelQuantization { get; set; } = QuantizationType.Q5_0;
    public bool UseGpu { get; set; } = true;
    public string Language { get; set; } = "en";
    /// <summary>Names and jargon, comma-separated, so Whisper spells them right.</summary>
    public string Vocabulary { get; set; } = "";

    /// <summary>Word fixes applied after cleanup, e.g. "git hub" → "GitHub".</summary>
    public List<Replacement> Replacements { get; set; } = new();

    /// <summary>Spoken shortcuts for longer text, e.g. "my email" → an address.</summary>
    public List<Snippet> Snippets { get; set; } = new();

    /// <summary>Looks for a new version on GitHub once a day and downloads it in the background.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Per-app overrides, matched by process name. Terminals get no trailing space by default.</summary>
    public List<AppProfile> AppProfiles { get; set; } =
    [
        new() { ProcessName = "WindowsTerminal", DisplayName = "Windows Terminal", AddTrailingSpace = false, JoinLines = true },
    ];

    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.BottomCenter;
    public OverlaySize OverlaySize { get; set; } = OverlaySize.Normal;
    /// <summary>Indicator opacity in percent.</summary>
    public int OverlayOpacity { get; set; } = 100;
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

    public List<WritingStyle> Styles { get; set; } = WritingStyle.Defaults();
    /// <summary>The usual writing style, by name.</summary>
    public string Style { get; set; } = WritingStyle.NaturalName;

    public WritingStyle StyleNamed(string? name) =>
        Styles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Styles.FirstOrDefault(s => s.Name == WritingStyle.NaturalName)
        ?? WritingStyle.Natural();

    public WritingStyle CurrentStyle => StyleNamed(Style);

    /// <summary>Brings settings saved by older versions up to date.</summary>
    public void Migrate()
    {
        foreach (var builtIn in WritingStyle.Defaults())
            if (!Styles.Any(s => string.Equals(s.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase)))
                Styles.Insert(Math.Min(Styles.Count, WritingStyle.Defaults().FindIndex(d => d.Name == builtIn.Name)), builtIn);

        // Before 2.5, "exact words" was the cleanup master switch and a per-app flag. It's a style now.
        if (!Cleanup.Enabled)
        {
            Style = WritingStyle.ExactName;
            Cleanup.Enabled = true;
        }
        foreach (var p in AppProfiles.Where(p => p.ExactWords))
        {
            p.Style ??= WritingStyle.ExactName;
            p.ExactWords = false;
        }
    }

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
        s.AppProfiles.Add(new AppProfile { ProcessName = "Code", DisplayName = "Visual Studio Code", Style = WritingStyle.ExactName, AddTrailingSpace = false });
        s.AppProfiles.Add(new AppProfile { ProcessName = "OUTLOOK", DisplayName = "Microsoft Outlook", Style = WritingStyle.FormalName });
        s.AppProfiles.Add(new AppProfile { ProcessName = "WhatsApp", DisplayName = "WhatsApp", Language = "es" });
        s.Snippets.AddRange(
        [
            new() { Trigger = "my email", Text = "sam@example.com" },
            new() { Trigger = "my sign off", Text = "Best regards,\nSam" },
        ]);
        s.Style = WritingStyle.FormalName;
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
        if (!string.IsNullOrWhiteSpace(p.Language)) s.Language = p.Language;
        if (p.JoinLines is { } join) s.JoinLines = join;
        if (p.ExactWords) s.Style = WritingStyle.ExactName; // unmigrated pre-2.5 profile
        if (p.Style != null && s.Styles.Any(st => string.Equals(st.Name, p.Style, StringComparison.OrdinalIgnoreCase)))
            s.Style = p.Style;
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
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
                loaded.Migrate();
                return loaded;
            }
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
        File.WriteAllText(AppPaths.SettingsFile, ToJson());
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

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
