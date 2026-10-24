using System.Diagnostics;
using NAudio.Wave;
using Whisper.net.LibraryLoader;

namespace BetterSTT;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // The speech engine checks itself in a separate process, so a crash in its native code can't take the app down.
        if (args is ["--probe-engine", var mode, var resultFile])
        {
            Environment.Exit(EngineCheck.RunProbe(mode == "gpu", resultFile));
            return;
        }

        // --cpu: never use the graphics card (a fix if the speech engine crashes on it). It stays off until turned on under Speech.
        bool forceCpu = args.Contains("--cpu");
        string[] plain = args.Where(a => a != "--cpu").ToArray();
        if (plain is ["--transcribe", var wav, var outFile])
        {
            TranscribeFile(wav, outFile, forceCpu);
            return;
        }

        // A restart from Settings waits for the old instance to exit first.
        if (args is ["--restart-after", var pidText, ..] && int.TryParse(pidText, out int pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(10_000); } catch { /* already gone */ }
        }

        string? screenshotDir = args is ["--screenshots", var dir] ? dir : null;
        bool background = args.Contains("--background");
        bool testRun = args.Contains("--no-paste");
        TextInjector.ClipboardOnly = testRun;
        int testAudio = Array.IndexOf(args, "--test-audio");
        if (testRun && testAudio >= 0 && testAudio + 1 < args.Length) AudioRecorder.TestInput = args[testAudio + 1];
        // --toggle, --paste-last, --cancel: control the running copy from a launcher, Stream Deck or script.
        string? command = args.Select(a => a.TrimStart('-').ToLowerInvariant()).FirstOrDefault(Signals.Commands.Contains);

        using var mutex = new Mutex(true, @"Local\BetterSTT.SingleInstance" + AppPaths.InstanceSuffix, out bool firstInstance);
        if (!firstInstance && screenshotDir == null)
        {
            // Already running: pass the command on, or ask that instance to open its window.
            Signals.Send(command ?? Signals.Show);
            return;
        }
        using var signals = new Signals(named: screenshotDir == null);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Write($"Background error: {e.Exception.GetBaseException()}");

        AppPaths.MigrateFromOldNames();

        // Screenshots use default settings and sample history so nothing personal ends up in them.
        var settings = screenshotDir == null ? AppSettings.Load() : AppSettings.Sample();

        if (forceCpu && settings.UseGpu)
        {
            settings.UseGpu = false;
            try { settings.Save(); } catch { /* the setting still applies to this run */ }
        }
        if (screenshotDir == null) Log.Write($"BetterSTT {Updater.Current.ToString(3)} starting · {EngineCheck.SystemSummary()}");

        // A downloaded update installs when the app is opened. The installer reopens BetterSTT afterwards.
        if (screenshotDir == null && !testRun && command == null && settings.CheckForUpdates
            && Updater.Ready() is { } update && Updater.ShouldAutoInstall(update)
            && Updater.Install(update, openWindow: !background))
        {
            return;
        }

        UseRuntime(settings);

        var app = new App(settings, background || command != null, signals, screenshotDir, command, args.Contains("--updated"));
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>Must run before the first WhisperFactory is created. Falls back to CPU if Vulkan is unavailable.</summary>
    internal static void UseRuntime(AppSettings settings)
    {
        try
        {
            RuntimeOptions.RuntimeLibraryOrder = settings.UseGpu
                ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
                : [RuntimeLibrary.Cpu];
        }
        catch (Exception ex)
        {
            // Not fatal: the engine check reports the same problem in plain words when the model loads.
            Log.Write($"Could not choose the speech runtime: {ex.Message}");
        }
    }

    /// <summary>A crash in managed code: keep the details and say so, instead of vanishing.</summary>
    static void ReportFatal(object error)
    {
        try
        {
            Log.Write($"Fatal: {error}");
            string file = Path.Combine(AppPaths.Data, "crash.txt");
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(file, $"BetterSTT {Updater.Current.ToString(3)} · {DateTime.Now:u}\n{EngineCheck.SystemSummary()}\n\n{error}");
            if (Environment.GetEnvironmentVariable("BETTERSTT_QUIET") != "1")
                System.Windows.Forms.MessageBox.Show(
                    "BetterSTT hit an unexpected error and has to close.\n\nThe details were saved to:\n" + file
                    + "\n\nIf it keeps happening, start BetterSTT.exe with the --cpu option, and send that file to whoever is helping you.",
                    "BetterSTT", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing more can be done.
        }
    }

    public static void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--restart-after {Environment.ProcessId}")
        {
            UseShellExecute = false,
        });
    }

    /// <summary>Diagnostics: BetterSTT.exe --transcribe input.wav result.txt (16 kHz mono 16-bit WAV).</summary>
    static void TranscribeFile(string wav, string outFile, bool forceCpu)
    {
        var settings = AppSettings.Load();
        if (forceCpu) settings.UseGpu = false;
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

/// <summary>Named events another launch of BetterSTT uses to reach the copy that's already running.</summary>
public sealed class Signals : IDisposable
{
    public const string Show = "show";
    public static readonly string[] Commands = ["toggle", "paste-last", "cancel"];
    static readonly string[] All = [Show, .. Commands];

    // "ShowWindow" is the name versions before 2.6 listen on.
    static string EventName(string signal) =>
        (signal == Show ? @"Local\BetterSTT.ShowWindow" : $@"Local\BetterSTT.{signal}") + AppPaths.InstanceSuffix;

    readonly EventWaitHandle[] _handles;

    /// <param name="named">False for a private set that nothing else can signal (screenshot mode).</param>
    public Signals(bool named) =>
        _handles = All.Select(s => new EventWaitHandle(false, EventResetMode.AutoReset, named ? EventName(s) : null)).ToArray();

    public static void Send(string signal)
    {
        try { EventWaitHandle.OpenExisting(EventName(signal)).Set(); }
        catch { /* the running copy is too old to know this signal */ }
    }

    /// <summary>Blocks until a signal arrives and returns its name.</summary>
    public string Wait() => All[WaitHandle.WaitAny(_handles)];

    public void Dispose()
    {
        foreach (var h in _handles) h.Dispose();
    }
}
