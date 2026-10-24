using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterSTT;

/// <summary>What happened when the speech engine was tried on this PC. Remembered so the check runs once, not every start.</summary>
public sealed class EngineResult
{
    /// <summary>Runtime, graphics card, model file and app version, so a new card, model or version is checked again.</summary>
    public string Key { get; set; } = "";
    public bool Ok { get; set; }
    /// <summary>Why it failed, in plain words (empty when it worked).</summary>
    public string Detail { get; set; } = "";
    public DateTime When { get; set; }
}

/// <summary>
/// Makes sure the native speech engine can run on this PC before the app loads it. A crash inside that native code
/// can't be caught, so it's tried first in a separate process: if that process crashes, the app keeps running and
/// falls back (graphics card → processor) instead of dying every time it starts. A marker file also catches a crash
/// that only happens inside the app.
/// </summary>
public static class EngineCheck
{
    static string MarkerFile => Path.Combine(AppPaths.Data, "engine-loading.txt");
    const int KeepResults = 8;
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(4);

    public static string Key(bool gpu, AppSettings s) =>
        $"{(gpu ? "gpu" : "cpu")}|{(gpu ? SystemInfo.GpuName ?? "unknown" : "-")}|{Path.GetFileName(Transcriber.ModelPath(s.ModelType, s.ModelQuantization))}|{Updater.Current.ToString(3)}";

    // ---- the separate process ----

    /// <summary>BetterSTT.exe --probe-engine gpu|cpu result.txt: loads the model and runs once, then reports. Exit code 0 = fine.</summary>
    public static int RunProbe(bool gpu, string resultFile)
    {
        void Write(string text)
        {
            try { File.WriteAllText(resultFile, text); }
            catch { /* the exit code still tells the story */ }
        }

        try
        {
            // Test hook: pretend the engine crashed (0xC0000005, an access violation) in this mode.
            if ((Environment.GetEnvironmentVariable("BETTERSTT_FAKE_PROBE") ?? "").Split(',').Contains(gpu ? "gpu-crash" : "cpu-crash"))
                Environment.Exit(unchecked((int)0xC0000005));

            var settings = AppSettings.Load();
            settings.UseGpu = gpu;
            Program.UseRuntime(settings);
            using var transcriber = new Transcriber();
            transcriber.Load(settings); // loads the model and runs one short inference, like the app does
            Write($"OK {transcriber.RuntimeName}");
            return 0;
        }
        catch (Exception ex)
        {
            Write($"{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    // ---- the app side ----

    public static async Task<EngineResult> ProbeAsync(bool gpu, AppSettings settings)
    {
        string key = Key(gpu, settings);
        string resultFile = Path.Combine(AppPaths.Data, $"engine-probe-{(gpu ? "gpu" : "cpu")}.txt");
        int exitCode;
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            try { File.Delete(resultFile); } catch { /* a stale file is replaced below */ }
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
                $"--probe-engine {(gpu ? "gpu" : "cpu")} \"{resultFile}\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
            bool finished = await Task.Run(() => process.WaitForExit((int)ProbeTimeout.TotalMilliseconds));
            if (!finished)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return Failed(key, $"it didn't finish loading within {ProbeTimeout.TotalMinutes:0} minutes");
            }
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            return Failed(key, $"the check couldn't start ({ex.Message})");
        }

        string report = File.Exists(resultFile) ? File.ReadAllText(resultFile).Trim() : "";
        if (exitCode == 0 && report.StartsWith("OK", StringComparison.Ordinal))
        {
            Log.Write($"Speech engine check passed ({(gpu ? "graphics card" : "processor")}): {report}");
            return new EngineResult { Key = key, Ok = true, Detail = report, When = DateTime.Now };
        }
        return Failed(key, Describe(exitCode, report));
    }

    static EngineResult Failed(string key, string detail)
    {
        Log.Write($"Speech engine check failed ({key}): {detail}");
        return new EngineResult { Key = key, Ok = false, Detail = detail, When = DateTime.Now };
    }

    /// <summary>Turns an exit code and the engine's own message into a short plain explanation.</summary>
    public static string Describe(int exitCode, string report)
    {
        uint code = unchecked((uint)exitCode);
        string text = report ?? "";
        bool missingRuntime = Regex(text, @"VCRUNTIME|MSVCP|VCOMP|Unable to load DLL|DllNotFound|0x8007007E|0x8007007B|Failed to load native whisper")
                              || code is 0xC0000135 or 0xC0000139 or 0xC000007B;
        if (Regex(text, @"AVX")) return "this processor is too old for the speech engine (it needs AVX support)";
        if (code == 0xC000001D) return "this processor is missing an instruction the speech engine needs (AVX)";
        if (missingRuntime)
            return "Windows is missing the Microsoft Visual C++ runtime that the speech engine needs"
                   + (text.Length > 0 ? $" ({FirstLine(text)})" : "");
        if (code == 0xC0000005) return "the graphics driver or the engine crashed (access violation)";
        if (code == 3) return "the speech engine stopped itself, usually because the graphics card is out of memory";
        if (text.Length > 0) return FirstLine(text);
        return $"the speech engine closed unexpectedly (code 0x{code:X8})";
    }

    static bool Regex(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static string FirstLine(string text)
    {
        string line = text.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    /// <summary>The steps to try when the engine can't run at all, ready to show to the user.</summary>
    public static string Advice(string detail) =>
        Regex(detail, "Visual C\\+\\+|VCRUNTIME|MSVCP")
            ? "Install the Microsoft Visual C++ Redistributable from https://aka.ms/vs/17/release/vc_redist.x64.exe and start BetterSTT again."
            : Regex(detail, "AVX")
                ? "Speech recognition can't run on this PC's processor."
                : "Try updating Windows and your graphics driver, then start BetterSTT again. Save diagnostics on the General page, or send log.txt from the BetterSTT data folder, if it keeps happening.";

    // ---- remembered results ----

    public static EngineResult? Cached(AppSettings s, string key) => s.EngineChecks.FirstOrDefault(r => r.Key == key);

    public static void Store(AppSettings s, EngineResult result)
    {
        s.EngineChecks.RemoveAll(r => r.Key == result.Key);
        s.EngineChecks.Add(result);
        if (s.EngineChecks.Count > KeepResults) s.EngineChecks.RemoveRange(0, s.EngineChecks.Count - KeepResults);
    }

    /// <summary>Forgets a runtime's results, so it's tried again (when the user turns the graphics card back on).</summary>
    public static void Forget(AppSettings s, bool gpu) =>
        s.EngineChecks.RemoveAll(r => r.Key.StartsWith(gpu ? "gpu|" : "cpu|", StringComparison.Ordinal));

    // ---- crash marker ----

    /// <summary>Written just before the engine loads inside the app, removed once it has.</summary>
    public static void MarkLoading(bool gpu)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(MarkerFile, gpu ? "gpu" : "cpu");
        }
        catch { /* the guard is best effort */ }
    }

    public static void ClearMarker()
    {
        try { File.Delete(MarkerFile); }
        catch { /* already gone */ }
    }

    /// <summary>"gpu" or "cpu" if the previous run died while loading the engine (and clears the marker); otherwise null.</summary>
    public static string? TakeMarker()
    {
        try
        {
            if (!File.Exists(MarkerFile)) return null;
            string runtime = File.ReadAllText(MarkerFile).Trim();
            File.Delete(MarkerFile);
            return runtime is "gpu" or "cpu" ? runtime : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- facts for the log and diagnostics ----

    public static string SystemSummary() =>
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) · .NET {Environment.Version} · "
        + $"AVX2 {(System.Runtime.Intrinsics.X86.Avx2.IsSupported ? "yes" : "no")} · GPU {SystemInfo.GpuName ?? "unknown"}";
}
