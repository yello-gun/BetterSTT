using System.Diagnostics;
using NAudio.Wave;
using Whisper.net.LibraryLoader;

namespace BetterSTT;

static class Program
{
    const string ShowSignalName = @"Local\BetterSTT.ShowWindow";

    [STAThread]
    static void Main(string[] args)
    {
        if (args is ["--transcribe", var wav, var outFile])
        {
            TranscribeFile(wav, outFile);
            return;
        }

        // A restart from Settings waits for the old instance to exit first.
        if (args is ["--restart-after", var pidText, ..] && int.TryParse(pidText, out int pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(10_000); } catch { /* already gone */ }
        }

        string? screenshotDir = args is ["--screenshots", var dir] ? dir : null;
        bool background = args.Contains("--background");

        using var mutex = new Mutex(true, @"Local\BetterSTT.SingleInstance", out bool firstInstance);
        if (!firstInstance && screenshotDir == null)
        {
            // Already running: ask that instance to open its window instead.
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { /* old version without the signal */ }
            return;
        }
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, screenshotDir == null ? ShowSignalName : null);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");

        AppPaths.MigrateFromOldNames();

        // Screenshots use default settings and sample history so nothing personal ends up in them.
        var settings = screenshotDir == null ? AppSettings.Load() : new AppSettings();
        UseRuntime(settings);

        var app = new App(settings, background, showSignal, screenshotDir);
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>Must run before the first WhisperFactory is created. Falls back to CPU if Vulkan is unavailable.</summary>
    static void UseRuntime(AppSettings settings) =>
        RuntimeOptions.RuntimeLibraryOrder = settings.UseGpu
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];

    public static void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--restart-after {Environment.ProcessId}")
        {
            UseShellExecute = false,
        });
    }

    /// <summary>Diagnostics: BetterSTT.exe --transcribe input.wav result.txt (16 kHz mono 16-bit WAV).</summary>
    static void TranscribeFile(string wav, string outFile)
    {
        var settings = AppSettings.Load();
        UseRuntime(settings);
        try
        {
            using var reader = new WaveFileReader(wav);
            var provider = reader.ToSampleProvider();
            var samples = new float[reader.SampleCount];
            int read = provider.Read(samples, 0, samples.Length);

            Transcriber.EnsureModelAsync(settings, null, CancellationToken.None).GetAwaiter().GetResult();
            using var transcriber = new Transcriber();
            var sw = Stopwatch.StartNew();
            transcriber.Load(settings);
            long loadMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var audio = AudioPrep.Prepare(samples[..read]) ?? [];
            string raw = transcriber.TranscribeAsync(audio, settings, CancellationToken.None).GetAwaiter().GetResult();
            long runMs = sw.ElapsedMilliseconds;
            string clean = TextCleaner.Clean(raw, settings.Cleanup);
            File.WriteAllText(outFile,
                $"runtime: {transcriber.RuntimeName}\nload: {loadMs} ms\ntranscribe: {runMs} ms ({read / (double)AudioRecorder.SampleRate:F1} s of audio)\nraw:   {raw}\nclean: {clean}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile, ex.ToString());
        }
    }
}
