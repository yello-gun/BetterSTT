using System.Diagnostics;
using System.Windows.Threading;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace BetterSTT;

public enum DictationState { Idle, Recording, Transcribing }
public enum ModelState { Loading, Downloading, Ready, Sleeping, Failed }

public enum OutcomeKind
{
    Pasted,
    /// <summary>BetterSTT itself had focus, so the text went to the clipboard.</summary>
    Copied,
    /// <summary>The focused window (e.g. an admin app) blocks simulated typing; the text went to the clipboard.</summary>
    Blocked,
    PastedLast,
    /// <summary>A saved recording was transcribed after a failure or crash; the text went to the clipboard.</summary>
    Recovered,
    Cancelled,
    NoSpeech,
    Empty,
    /// <summary>Transcription failed; the recording is kept for a retry.</summary>
    Failed,
}

public sealed record DictationOutcome(OutcomeKind Kind, RecentDictation? Item = null, string? Error = null);

public enum HotkeyTarget { Dictate, PasteLast }

/// <summary>
/// Owns the dictation pipeline (hotkey → record → transcribe → clean → paste) and the settings.
/// Lives on the UI thread; every event is raised there.
/// </summary>
public sealed class DictationController : IDisposable
{
    const int DictateHotkeyId = 1, PasteLastHotkeyId = 2, CancelHotkeyId = 3;
    static readonly HotkeyBinding EscapeKey = new() { Key = WinFormsKeys.Escape, Ctrl = false, Alt = false };

    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly AudioRecorder _recorder = new();
    readonly Transcriber _transcriber = new();
    readonly GlobalHotkey _hotkey = new();
    readonly DispatcherTimer _saveTimer, _maxLengthTimer, _holdTimer, _idleTimer;
    readonly SemaphoreSlim _loadGate = new(1, 1);
    readonly bool _startedWithGpu;
    Task _modelTask = Task.CompletedTask;
    int _hotkeySuspensions;
    DateTime _recordingStarted;
    DateTime _lastUsed = DateTime.Now;
    string? _spoolPath;

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
        _maxLengthTimer.Tick += (_, _) => { if (State == DictationState.Recording) _ = StopAndTranscribeAsync(); };
        // Hold-to-talk: watch for the shortcut's key being released.
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _holdTimer.Tick += (_, _) =>
        {
            if (State != DictationState.Recording) { _holdTimer.Stop(); return; }
            if (!GlobalHotkey.IsKeyDown(Settings.Hotkey.Key)) _ = StopAndTranscribeAsync();
        };
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _idleTimer.Tick += (_, _) => _ = UnloadIfIdleAsync();

        _hotkey.Pressed += OnHotkey;
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
    public bool PasteLastRegistered { get; private set; }
    public TimeSpan Elapsed => State == DictationState.Recording ? DateTime.Now - _recordingStarted : TimeSpan.Zero;
    public double LastAudioSeconds { get; private set; }
    public double LastTranscribeSeconds { get; private set; }

    /// <summary>Recordings that failed to transcribe, or were interrupted by a crash, oldest first.</summary>
    public IReadOnlyList<string> PendingRecordings => PendingAudio.List(except: _spoolPath);

    /// <summary>The GPU setting differs from what this process started with; a restart applies it.</summary>
    public bool RestartNeeded => Settings.UseGpu != _startedWithGpu;

    public string StatusText => State switch
    {
        DictationState.Recording => "Listening",
        DictationState.Transcribing => "Transcribing",
        _ => ModelState switch
        {
            ModelState.Ready or ModelState.Sleeping => "Ready",
            ModelState.Downloading => $"Downloading model ({DownloadedMb} MB)",
            ModelState.Failed => "Model failed to load",
            _ => "Loading model",
        },
    };

    public event Action? StateChanged;
    public event Action? ModelChanged;
    public event Action? SettingsChanged;
    public event Action? PendingChanged;
    public event Action<float>? LevelChanged;
    public event Action<DictationOutcome>? DictationFinished;
    /// <summary>A message for the user (shown as a tray notification). The flag marks problems.</summary>
    public event Action<string, bool>? Notice;

    public void Start()
    {
        RegisterHotkeys(announceConflict: true);
        _modelTask = LoadModelAsync();
        _idleTimer.Start();

        int pending = PendingRecordings.Count;
        if (pending > 0)
            Notice?.Invoke(pending == 1
                ? "A dictation from last time wasn't transcribed. Open BetterSTT to retry it."
                : $"{pending} dictations from last time weren't transcribed. Open BetterSTT to retry them.", true);
    }

    // ---- hotkeys ----

    void OnHotkey(int id)
    {
        switch (id)
        {
            case DictateHotkeyId:
                if (Settings.Activation == ActivationMode.Hold)
                {
                    if (State == DictationState.Idle && StartRecording()) _holdTimer.Start();
                    else if (State == DictationState.Transcribing) Sounds.Error();
                }
                else
                {
                    Toggle();
                }
                break;
            case PasteLastHotkeyId:
                _ = PasteLastAsync();
                break;
            case CancelHotkeyId:
                _ = CancelAsync();
                break;
        }
    }

    void RegisterHotkeys(bool announceConflict)
    {
        if (_hotkeySuspensions > 0)
        {
            HotkeyRegistered = PasteLastRegistered = false;
            return;
        }

        HotkeyRegistered = _hotkey.Register(DictateHotkeyId, Settings.Hotkey);
        if (!HotkeyRegistered && announceConflict)
            Notice?.Invoke($"{Settings.Hotkey} is already used by another app. Choose a different shortcut under General.", true);

        _hotkey.Unregister(PasteLastHotkeyId);
        PasteLastRegistered = Settings.PasteLastEnabled && _hotkey.Register(PasteLastHotkeyId, Settings.PasteLastHotkey);
        if (Settings.PasteLastEnabled && !PasteLastRegistered && announceConflict)
            Notice?.Invoke($"{Settings.PasteLastHotkey} (paste last dictation) is already used by another app.", true);
    }

    /// <summary>Temporarily frees the hotkeys, only while a new shortcut is being recorded.</summary>
    public void SuspendHotkey()
    {
        _hotkeySuspensions++;
        _hotkey.Unregister(DictateHotkeyId);
        _hotkey.Unregister(PasteLastHotkeyId);
        HotkeyRegistered = PasteLastRegistered = false;
    }

    public void ResumeHotkey()
    {
        if (_hotkeySuspensions > 0) _hotkeySuspensions--;
        if (_hotkeySuspensions == 0) RegisterHotkeys(announceConflict: true);
        SettingsChanged?.Invoke();
    }

    /// <summary>Saves a new shortcut. Returns null on success, otherwise why it can't be used.</summary>
    public string? TrySetHotkey(HotkeyTarget target, HotkeyBinding binding)
    {
        var other = target == HotkeyTarget.Dictate ? Settings.PasteLastHotkey : Settings.Hotkey;
        bool otherInUse = target == HotkeyTarget.Dictate ? Settings.PasteLastEnabled : true;
        if (otherInUse && binding.SameAs(other))
            return $"{binding} is already your {(target == HotkeyTarget.Dictate ? "paste-last" : "dictation")} shortcut.";
        if (binding.Key == WinFormsKeys.Escape && !binding.Ctrl && !binding.Alt && !binding.Shift && !binding.Win)
            return "Esc is reserved for cancelling a dictation.";
        if (!_hotkey.IsAvailable(binding))
            return $"{binding} is already used by another app. Try another combination.";

        Update(s =>
        {
            if (target == HotkeyTarget.Dictate) s.Hotkey = binding;
            else s.PasteLastHotkey = binding;
        });
        return null;
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
        if (!before.Hotkey.SameAs(Settings.Hotkey)
            || !before.PasteLastHotkey.SameAs(Settings.PasteLastHotkey)
            || before.PasteLastEnabled != Settings.PasteLastEnabled)
        {
            RegisterHotkeys(announceConflict: true);
        }
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
            _lastUsed = DateTime.Now;
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

    /// <summary>Frees the model's memory once it has been idle for the configured time.</summary>
    async Task UnloadIfIdleAsync()
    {
        int minutes = Settings.UnloadModelAfterMinutes;
        if (minutes <= 0 || State != DictationState.Idle || ModelState != ModelState.Ready) return;
        if (DateTime.Now - _lastUsed < TimeSpan.FromMinutes(minutes)) return;

        await _loadGate.WaitAsync();
        try
        {
            if (State != DictationState.Idle || ModelState != ModelState.Ready) return;
            await Task.Run(_transcriber.Unload);
            SetModelState(ModelState.Sleeping);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Starts reloading a sleeping model (or retries a failed one) as soon as it will be needed.</summary>
    void WakeModel()
    {
        if (ModelState == ModelState.Sleeping || _modelTask.IsFaulted) _modelTask = LoadModelAsync();
    }

    async Task EnsureModelReadyAsync()
    {
        WakeModel();
        if (ModelState != ModelState.Ready) await _modelTask;
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

    bool StartRecording()
    {
        _spoolPath = PendingAudio.NewPath();
        try
        {
            _recorder.Start(Settings.MicrophoneDevice, _spoolPath);
        }
        catch (Exception ex)
        {
            PendingAudio.Delete(_spoolPath);
            _spoolPath = null;
            Log.Write($"Microphone error: {ex}");
            Sounds.Error();
            Notice?.Invoke($"Could not open the microphone: {ex.Message}", true);
            return false;
        }

        _recordingStarted = DateTime.Now;
        State = DictationState.Recording;
        Sounds.Start();
        WakeModel(); // a sleeping model reloads while you talk
        _hotkey.Register(CancelHotkeyId, EscapeKey); // Esc cancels, only while listening
        _maxLengthTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.MaxRecordingMinutes));
        _maxLengthTimer.Start();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Stops listening and throws the recording away without typing anything.</summary>
    public async Task CancelAsync()
    {
        if (State != DictationState.Recording) return;
        EndRecordingControls();
        State = DictationState.Transcribing; // blocks new starts until the microphone is closed
        StateChanged?.Invoke();

        await _recorder.StopAsync();
        PendingAudio.Delete(_spoolPath);
        _spoolPath = null;
        Sounds.Stop();

        State = DictationState.Idle;
        StateChanged?.Invoke();
        Finish(new DictationOutcome(OutcomeKind.Cancelled));
    }

    /// <summary>Reports the outcome to the UI and notes its kind (never the text) in the log.</summary>
    void Finish(DictationOutcome outcome)
    {
        Log.Write($"Outcome: {outcome.Kind}");
        DictationFinished?.Invoke(outcome);
    }

    void EndRecordingControls()
    {
        _maxLengthTimer.Stop();
        _holdTimer.Stop();
        _hotkey.Unregister(CancelHotkeyId);
    }

    async Task StopAndTranscribeAsync()
    {
        if (State != DictationState.Recording) return;
        EndRecordingControls();
        State = DictationState.Transcribing;
        Sounds.Stop();
        StateChanged?.Invoke();

        // Keep listening a moment longer so the last word isn't clipped.
        int tail = Math.Clamp(Settings.TailCaptureMs, 0, 1000);
        if (tail > 0) await Task.Delay(tail);

        string? spool = _spoolPath;
        DictationOutcome outcome;
        try
        {
            var samples = await _recorder.StopAsync();
            _spoolPath = null;
            outcome = await TranscribeAndDeliverAsync(samples);
            PendingAudio.Delete(spool); // transcribed (or nothing to transcribe): the recording is no longer needed
        }
        catch (Exception ex)
        {
            _spoolPath = null;
            Log.Write($"Dictation failed: {ex}");
            Sounds.Error();
            bool kept = spool != null && File.Exists(spool);
            Notice?.Invoke(kept
                ? $"Dictation failed: {ex.Message} The recording was saved; you can retry it from the Home page."
                : $"Dictation failed: {ex.Message}", true);
            outcome = new DictationOutcome(OutcomeKind.Failed, Error: ex.Message);
            PendingChanged?.Invoke();
        }

        State = DictationState.Idle;
        StateChanged?.Invoke();
        Finish(outcome);
    }

    async Task<DictationOutcome> TranscribeAndDeliverAsync(float[] samples)
    {
        var result = await TranscribeAsync(samples);
        if (result == null) return new DictationOutcome(OutcomeKind.NoSpeech);
        var (raw, clean) = result.Value;
        if (clean.Length == 0) return new DictationOutcome(OutcomeKind.Empty);

        var item = History.Add(raw, clean);
        Log.Write($"Dictation: {LastAudioSeconds:F1} s audio, {LastTranscribeSeconds:F2} s to transcribe, {item.WordsRemoved} words removed");

        var kind = await DeliverAsync(Settings.AddTrailingSpace ? clean + " " : clean, clean);
        return new DictationOutcome(kind, item);
    }

    /// <returns>Null when there was no speech in the recording.</returns>
    async Task<(string Raw, string Clean)?> TranscribeAsync(float[] samples)
    {
        var audio = AudioPrep.Prepare(samples);
        if (audio == null) return null;

        await EnsureModelReadyAsync();

        var settings = Settings.Clone();
        var timer = Stopwatch.StartNew();
        string raw = await Task.Run(() => _transcriber.TranscribeAsync(audio, settings, CancellationToken.None));
        LastTranscribeSeconds = timer.Elapsed.TotalSeconds;
        LastAudioSeconds = audio.Length / (double)AudioRecorder.SampleRate;
        _lastUsed = DateTime.Now;
        return (raw, TextCleaner.Clean(raw, settings.Cleanup));
    }

    /// <summary>Types or pastes the text, falling back to the clipboard where typing can't reach.</summary>
    async Task<OutcomeKind> DeliverAsync(string text, string clipboardText)
    {
        // Started from BetterSTT's own window (or it has focus): there is nothing to paste into.
        if (TextInjector.IsOwnWindowFocused())
        {
            TextInjector.SetClipboard(clipboardText);
            return OutcomeKind.Copied;
        }
        if (TextInjector.IsForegroundBlocked())
        {
            TextInjector.SetClipboard(clipboardText);
            if (!Settings.ShowOverlay)
                Notice?.Invoke("That window is running as administrator, so it blocks typing from other apps. Your text is on the clipboard; press Ctrl+V.", false);
            return OutcomeKind.Blocked;
        }
        await TextInjector.InjectAsync(text, Settings);
        return OutcomeKind.Pasted;
    }

    // ---- paste last / recovery ----

    public async Task PasteLastAsync()
    {
        var last = History.Recent.FirstOrDefault();
        if (State != DictationState.Idle || last == null)
        {
            Sounds.Error();
            return;
        }

        var kind = await DeliverAsync(Settings.AddTrailingSpace ? last.Clean + " " : last.Clean, last.Clean);
        Finish(new DictationOutcome(kind == OutcomeKind.Pasted ? OutcomeKind.PastedLast : kind, last));
    }

    /// <summary>Transcribes a saved recording and puts the text on the clipboard.</summary>
    public async Task RetryPendingAsync(string path)
    {
        if (State != DictationState.Idle)
        {
            Sounds.Error();
            return;
        }
        State = DictationState.Transcribing;
        StateChanged?.Invoke();

        DictationOutcome outcome;
        try
        {
            var result = await TranscribeAsync(PendingAudio.Read(path));
            if (result == null)
            {
                outcome = new DictationOutcome(OutcomeKind.NoSpeech);
            }
            else if (result.Value.Clean.Length == 0)
            {
                outcome = new DictationOutcome(OutcomeKind.Empty);
            }
            else
            {
                var item = History.Add(result.Value.Raw, result.Value.Clean);
                TextInjector.SetClipboard(result.Value.Clean);
                outcome = new DictationOutcome(OutcomeKind.Recovered, item);
            }
            PendingAudio.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Write($"Retry failed: {ex}");
            Sounds.Error();
            Notice?.Invoke($"Still couldn't transcribe that recording: {ex.Message}", true);
            outcome = new DictationOutcome(OutcomeKind.Failed, Error: ex.Message);
        }

        State = DictationState.Idle;
        StateChanged?.Invoke();
        PendingChanged?.Invoke();
        Finish(outcome);
    }

    public void DiscardPending(string path)
    {
        PendingAudio.Delete(path);
        PendingChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_saveTimer.IsEnabled) SaveSettings();
        _idleTimer.Stop();
        _hotkey.Dispose();
        _recorder.Dispose();
        _transcriber.Dispose();
    }
}
