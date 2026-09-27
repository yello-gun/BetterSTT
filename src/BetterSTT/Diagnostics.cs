using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterSTT;

/// <summary>
/// Gathers what's needed for a bug report into one .zip: app, Windows and GPU details, the log and the
/// settings. Nothing dictated is included, and personal text in the settings is replaced.
/// </summary>
public static class Diagnostics
{
    public const string Hidden = "(hidden)";

    /// <summary>The files a bundle contains, for showing before it's saved.</summary>
    public static readonly string[] Contents = ["about.txt", "settings.json", "log.txt"];

    public static void Save(string zipPath, DictationController c)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        Add(zip, "about.txt", Report(c));
        Add(zip, "settings.json", Redact(RedactedSettings(c.Settings).ToJson()));
        string log = "";
        try { if (File.Exists(AppPaths.LogFile)) log = File.ReadAllText(AppPaths.LogFile); }
        catch (IOException) { /* being written; leave it out */ }
        Add(zip, "log.txt", Redact(log));
    }

    static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    public static string Report(DictationController c)
    {
        var s = c.Settings;
        var model = AppSettings.Models.FirstOrDefault(m => m.Type == s.ModelType && m.Quantization == s.ModelQuantization);
        int mics = 0;
        try { mics = AudioRecorder.GetDevices().Count; } catch { /* no audio subsystem */ }

        return new StringBuilder()
            .AppendLine($"BetterSTT {Updater.Current.ToString(3)}")
            .AppendLine($"Saved: {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine($"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})")
            .AppendLine($".NET: {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"GPU: {SystemInfo.GpuName ?? "unknown"}")
            .AppendLine($"Speech runtime: {c.RuntimeName} (GPU {(s.UseGpu ? "on" : "off")})")
            .AppendLine($"Model: {model?.Name ?? $"{s.ModelType} {s.ModelQuantization}"} ({(Transcriber.IsDownloaded(s) ? "downloaded" : "not downloaded")}), state {c.ModelState}{(c.ModelError != null ? $": {c.ModelError}" : "")}")
            .AppendLine($"Last dictation: {c.LastAudioSeconds:F1} s of audio in {c.LastTranscribeSeconds:F2} s")
            .AppendLine($"Memory in use: {Environment.WorkingSet >> 20} MB")
            .AppendLine($"Microphones: {mics} (using {(s.MicrophoneDevice < 0 ? "Windows default" : $"#{s.MicrophoneDevice}")})")
            .AppendLine($"Shortcut: {s.Hotkey} ({(c.HotkeyRegistered ? "working" : "not registered")}), {s.Activation}")
            .AppendLine($"Paste last: {(s.PasteLastEnabled ? $"{s.PasteLastHotkey} ({(c.PasteLastRegistered ? "working" : "not registered")})" : "off")}")
            .AppendLine($"Saved recordings waiting: {c.PendingRecordings.Count}")
            .AppendLine($"Word fixes: {s.Replacements.Count}, snippets: {s.Snippets.Count}, app rules: {s.AppProfiles.Count}")
            .ToString();
    }

    /// <summary>A copy of the settings with the user's own words replaced: vocabulary, word fixes and snippets.</summary>
    public static AppSettings RedactedSettings(AppSettings settings)
    {
        var copy = settings.Clone();
        if (copy.Vocabulary.Length > 0) copy.Vocabulary = Hidden;
        foreach (var r in copy.Replacements) r.From = r.To = Hidden;
        foreach (var sn in copy.Snippets) sn.Trigger = sn.Text = Hidden;
        return copy;
    }

    /// <summary>Replaces the Windows user name and profile folder, which appear in file paths.</summary>
    public static string Redact(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName;
        if (user.Length > 2) text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
