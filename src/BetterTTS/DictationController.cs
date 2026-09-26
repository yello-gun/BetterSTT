using System.Diagnostics;
using System.Windows.Threading;

namespace BetterTTS;

public enum DictationState { Idle, Recording, Transcribing }
public enum ModelState { Loading, Downloading, Ready, Failed }
public enum OutcomeKind { Pasted, Copied, NoSpeech, Empty, Failed }

public sealed record DictationOutcome(OutcomeKind Kind, RecentDictation? Item = null, string? Error = null);

/// <summary>
/// Owns the dictation pipeline (hotkey → record → transcribe → clean → paste) and the settings.
/// Lives on the UI thread; every event is raised there.
/// </summary>
public sealed class DictationController : IDisposable
{
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly AudioRecorder _recorder = new();
    readonly Transcriber _transcriber = new();
    readonly GlobalHotkey _hotkey = new();
    readonly DispatcherTimer _saveTimer, _maxLengthTimer;
    readonly SemaphoreSlim _loadGate = new(1, 1);
    readonly bool _startedWithGpu;
    Task _modelTask = Task.CompletedTask;
    int _hotkeySuspensions;
    DateTime _recordingStarted;

    public DictationController(AppSettings settings, HistoryStore? history = null)
    {
        Settings = settings;
        History = history ?? HistoryStore.Load();
        _startedWithGpu = settings.UseGpu;
        try { Settings.StartWithWindows = StartupRegistration.IsEnabled(); } catch { /* registry unavailable */ }
        Sounds.Enabled = settings.PlaySounds;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
        _maxLengthTimer = new DispatcherTimer();
        _maxLengthTimer.Tick += (_, _) => { if (State == DictationState.Recording) Toggle(); };

        _hotkey.Pressed += Toggle;
        _recorder.LevelChanged += level => _ui.BeginInvoke(() => LevelChanged?.Invoke(level));
    }

    public AppSettings Settings { get; }
    public HistoryStore History { get; }
    public DictationState State { get; private set; }
    public ModelState ModelState { get; private set; } = ModelState.Loading;
    public long DownloadedMb { get; private set; }
    public string? ModelError { get; private set; }
    public string RuntimeName => _transcriber.RuntimeName;
    public bool HotkeyRegistered { get; private set; }
    public TimeSpan Elapsed => State == DictationState.Recording ? DateTime.Now - _recordingStarted : TimeSpan.Zero;
    public double LastAudioSeconds { get; private set; }
    public double LastTranscribeSeconds { get; private set; }

    /// <summary>The GPU setting differs from what this process started with; a restart applies it.</summary>
    public bool RestartNeeded => Settings.UseGpu != _startedWithGpu;

    public string StatusText => State switch
    {
        DictationState.Recording => "Listening",
        DictationState.Transcribing => "Transcribing",
        _ => ModelState switch
        {
            ModelState.Ready => "Ready",
            ModelState.Downloading => $"Downloading model ({DownloadedMb} MB)",
            ModelState.Failed => "Model failed to load",
            _ => "Loading model",
        },
    };

    public event Action? StateChanged;
    public event Action? ModelChanged;
    public event Action? SettingsChanged;
    public event Action<float>? LevelChanged;
    public event Action<DictationOutcome>? DictationFinished;
    /// <summary>A message for the user (shown as a tray notification). The flag marks problems.</summary>
    public event Action<string, bool>? Notice;

    public void Start()
    {
        RegisterHotkey(announceConflict: true);
        _modelTask = LoadModelAsync();
    }

    // ---- hotkey ----

    void RegisterHotkey(bool announceConflict)
    {
        HotkeyRegistered = _hotkeySuspensions == 0 && _hotkey.Register(Settings.Hotkey);
        if (_hotkeySuspensions == 0 && !HotkeyRegistered && announceConflict)
            Notice?.Invoke($"{Settings.Hotkey} is already used by another app. Choose a different shortcut under General.", true);
    }

    /// <summary>Temporarily frees the hotkey, only while a new shortcut is being recorded.</summary>
    public void SuspendHotkey()
    {
        _hotkeySuspensions++;
        _hotkey.Unregister();
        HotkeyRegistered = false;
    }

    public void ResumeHotkey()
    {
        if (_hotkeySuspensions > 0) _hotkeySuspensions--;
        if (_hotkeySuspensions == 0) RegisterHotkey(announceConflict: true);
        SettingsChanged?.Invoke();
    }

    /// <summary>Saves a new shortcut if Windows lets us register it; returns false if another app owns it.</summary>
    public bool TrySetHotkey(HotkeyBinding binding)
    {
        bool available = _hotkey.Register(binding);
        _hotkey.Unregister();
        if (!available) return false;
        Update(s => s.Hotkey = binding);
        return true;
    }

    // ---- settings ----

    /// <summary>Changes settings, applies side effects immediately and saves shortly after.</summary>
    public void Update(Action<AppSettings> change)
    {
        var before = Settings.Clone();
        change(Settings);

        Sounds.Enabled = Settings.PlaySounds;
        if (before.StartWithWindows != Settings.StartWithWindows)
        {
            try { StartupRegistration.Apply(Settings.StartWithWindows); }
            catch (Exception ex) { Log.Write($"Startup registration failed: {ex.Message}"); }
        }
        if (!before.Hotkey.SameAs(Settings.Hotkey)) RegisterHotkey(announceConflict: true);
        if (before.ModelType != Settings.ModelType || before.ModelQuantization != Settings.ModelQuantization)
            _modelTask = LoadModelAsync();

        _saveTimer.Stop();
        _saveTimer.Start();
        SettingsChanged?.Invoke();
    }

    void SaveSettings()
    {
        try { Settings.Save(); }
        catch (Exception ex) { Notice?.Invoke($"Settings could not be saved: {ex.Message}", true); }
    }

    public void ClearHistory()
    {
        History.ClearRecent();
        SettingsChanged?.Invoke();
    }

    // ---- model ----

    async Task LoadModelAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            var snapshot = Settings.Clone();
            ModelError = null;
            SetModelState(ModelState.Loading);
            if (!Transcriber.IsDownloaded(snapshot))
            {
                DownloadedMb = 0;
                SetModelState(ModelState.Downloading);
                var progress = new Progress<long>(mb => { DownloadedMb = mb; ModelChanged?.Invoke(); });
                await Task.Run(() => Transcriber.EnsureModelAsync(snapshot, progress, CancellationToken.None));
                SetModelState(ModelState.Loading);
            }
            await Task.Run(() => _transcriber.Load(snapshot));
            SetModelState(ModelState.Ready);
        }
        catch (Exception ex)
        {
            Log.Write($"Model load failed: {ex}");
            ModelError = ex.Message;
            SetModelState(ModelState.Failed);
            Notice?.Invoke($"Could not load the speech model: {ex.Message}", true);
            throw;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    void SetModelState(ModelState state)
    {
        ModelState = state;
        ModelChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // ---- dictation ----

    public void Toggle()
    {
        switch (State)
        {
            case DictationState.Idle: StartRecording(); break;
            case DictationState.Recording: _ = StopAndTranscribeAsync(); break;
            default: Sounds.Error(); break;
        }
    }

    void StartRecording()
    {
        try
        {
            _recorder.Start(Settings.MicrophoneDevice);
        }
        catch (Exception ex)
        {
            Log.Write($"Microphone error: {ex}");
            Sounds.Error();
            Notice?.Invoke($"Could not open the microphone: {ex.Message}", true);
            return;
        }

        _recordingStarted = DateTime.Now;
        State = DictationState.Recording;
        Sounds.Start();
        _maxLengthTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.MaxRecordingMinutes));
        _maxLengthTimer.Start();
        StateChanged?.Invoke();
    }

    async Task StopAndTranscribeAsync()
    {
        _maxLengthTimer.Stop();
        State = DictationState.Transcribing;
        Sounds.Stop();
        StateChanged?.Invoke();

        DictationOutcome outcome;
        try
        {
            outcome = await TranscribeAndDeliverAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Dictation failed: {ex}");
            Sounds.Error();
            Notice?.Invoke($"Dictation failed: {ex.Message}", true);
            outcome = new DictationOutcome(OutcomeKind.Failed, Error: ex.Message);
        }

        State = DictationState.Idle;
        StateChanged?.Invoke();
        DictationFinished?.Invoke(outcome);
    }

    async Task<DictationOutcome> TranscribeAndDeliverAsync()
    {
        var audio = AudioPrep.Prepare(await _recorder.StopAsync());
        if (audio == null) return new DictationOutcome(OutcomeKind.NoSpeech);

        if (ModelState != ModelState.Ready)
        {
            if (_modelTask.IsFaulted) _modelTask = LoadModelAsync();
            await _modelTask;
        }

        var settings = Settings.Clone();
        var timer = Stopwatch.StartNew();
        string raw = await Task.Run(() => _transcriber.TranscribeAsync(audio, settings, CancellationToken.None));
        LastTranscribeSeconds = timer.Elapsed.TotalSeconds;
        LastAudioSeconds = audio.Length / (double)AudioRecorder.SampleRate;

        string clean = TextCleaner.Clean(raw, settings.Cleanup);
        if (clean.Length == 0) return new DictationOutcome(OutcomeKind.Empty);

        var item = History.Add(raw, clean);
        Log.Write($"Dictation: {LastAudioSeconds:F1} s audio, {LastTranscribeSeconds:F2} s to transcribe, {item.WordsRemoved} words removed");

        // Started from BetterTTS's own window (or it has focus): there is nothing to paste into.
        if (TextInjector.IsOwnWindowFocused())
        {
            TextInjector.SetClipboard(clean);
            return new DictationOutcome(OutcomeKind.Copied, item);
        }

        await TextInjector.InjectAsync(settings.AddTrailingSpace ? clean + " " : clean, settings);
        return new DictationOutcome(OutcomeKind.Pasted, item);
    }

    public void Dispose()
    {
        if (_saveTimer.IsEnabled) SaveSettings();
        _hotkey.Dispose();
        _recorder.Dispose();
        _transcriber.Dispose();
    }
}
