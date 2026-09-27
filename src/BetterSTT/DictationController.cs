using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Net.Http;
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

public enum HotkeyTarget { Dictate, PasteLast, AddWord }

/// <summary>
/// Owns the dictation pipeline (hotkey → record → transcribe → clean → paste) and the settings.
/// Lives on the UI thread; every event is raised there.
/// </summary>
public sealed class DictationController : IDisposable
{
    const int DictateHotkeyId = 1, PasteLastHotkeyId = 2, CancelHotkeyId = 3, AddWordHotkeyId = 4;
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
    AppInfo? _target;
    readonly DispatcherTimer _previewTimer;
    CancellationTokenSource _previewCts = new();
    bool _previewBusy;
    readonly DispatcherTimer _updateTimer;

    // Hands-free: the session is transcribed in pieces at pauses, and typed as a whole at the end.
    readonly DispatcherTimer _chunkTimer;
    readonly List<string> _chunks = new();
    Task _chunkChain = Task.CompletedTask;
    bool _handsFree, _chunkFailed;
    int _session;
    const int MinChunkSeconds = 8, MaxChunkSeconds = 60;

    public DictationController(AppSettings settings, HistoryStore? history = null)
    {
        Settings = settings;
        ActiveSettings = settings.Clone();
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
            if (!_hotkey.IsDown(Settings.Hotkey.Key)) _ = StopAndTranscribeAsync();
        };
        _chunkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _chunkTimer.Tick += (_, _) => CutChunkAtPause();
        _previewTimer = new DispatcherTimer();
        _previewTimer.Tick += (_, _) => _ = UpdatePreviewAsync();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _idleTimer.Tick += (_, _) => _ = UnloadIfIdleAsync();
        // The first update check waits until startup has settled, then it's looked at hourly.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);
            bool due = Settings.LastUpdateCheck is not { } last || DateTime.Now - last > TimeSpan.FromHours(24);
            if (Settings.CheckForUpdates && due) _ = CheckForUpdatesAsync();
        };

        _hotkey.Pressed += OnHotkey;
        _recorder.LevelChanged += level => _ui.BeginInvoke(() => LevelChanged?.Invoke(level));
    }

    public AppSettings Settings { get; }

    /// <summary>
    /// The settings for the dictation in progress (or the last one): <see cref="Settings"/> with the
    /// target app's profile applied.
    /// </summary>
    public AppSettings ActiveSettings { get; private set; }

    /// <summary>The app the current (or last) dictation is going into, if known.</summary>
    public AppInfo? TargetApp => _target;

    public HistoryStore History { get; }
    public DictationState State { get; private set; }
    public ModelState ModelState { get; private set; } = ModelState.Loading;
    public long DownloadedMb { get; private set; }
    public string? ModelError { get; private set; }
    public string RuntimeName => _transcriber.RuntimeName;
    public bool HotkeyRegistered { get; private set; }
    public bool PasteLastRegistered { get; private set; }
    public bool AddWordRegistered { get; private set; }

    /// <summary>The add-a-word shortcut was pressed; carries the text that was selected (null if nothing was).</summary>
    public event Action<string?>? AddWordRequested;

    /// <summary>The most recent dictation, including one made in private mode (which isn't in <see cref="History"/>).</summary>
    public RecentDictation? LastDictation => _lastPrivate ?? History.Recent.FirstOrDefault();
    RecentDictation? _lastPrivate;
    public TimeSpan Elapsed => State == DictationState.Recording ? DateTime.Now - _recordingStarted : TimeSpan.Zero;
    public double LastAudioSeconds { get; private set; }
    public double LastTranscribeSeconds { get; private set; }

    /// <summary>Recordings that failed to transcribe, or were interrupted by a crash, oldest first.</summary>
    public IReadOnlyList<string> PendingRecordings => PendingAudio.List(except: _spoolPath);

    /// <summary>The GPU setting differs from what this process started with; a restart applies it.</summary>
    public bool RestartNeeded => Settings.UseGpu != _startedWithGpu;

    public string StatusText => State switch
    {
        DictationState.Recording => _handsFree ? "Listening (hands-free)" : "Listening",
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
    /// <summary>The live draft (<see cref="PreviewText"/>) changed while recording.</summary>
    public event Action? PreviewChanged;

    /// <summary>A draft of what's being said, updated while recording; empty otherwise.</summary>
    public string PreviewText { get; private set; } = "";
    public event Action<float>? LevelChanged;
    public event Action<DictationOutcome>? DictationFinished;
    /// <summary>A message for the user (shown as a tray notification). The flag marks problems.</summary>
    public event Action<string, bool>? Notice;

    /// <param name="checkForUpdates">False for screenshots and test runs, which must not touch the network or settings.</param>
    public void Start(bool checkForUpdates = true)
    {
        RegisterHotkeys(announceConflict: true);
        _modelTask = LoadModelAsync();
        _idleTimer.Start();
        if (checkForUpdates)
        {
            AvailableUpdate = Updater.Ready();
            _updateTimer.Start();
        }

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
            case AddWordHotkeyId:
                _ = RequestAddWordAsync();
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
            HotkeyRegistered = PasteLastRegistered = AddWordRegistered = false;
            return;
        }

        HotkeyRegistered = _hotkey.Register(DictateHotkeyId, Settings.Hotkey);
        if (!HotkeyRegistered && announceConflict)
            Notice?.Invoke($"{Settings.Hotkey} is already used by another app. Choose a different shortcut under General.", true);

        _hotkey.Unregister(PasteLastHotkeyId);
        PasteLastRegistered = Settings.PasteLastEnabled && _hotkey.Register(PasteLastHotkeyId, Settings.PasteLastHotkey);
        if (Settings.PasteLastEnabled && !PasteLastRegistered && announceConflict)
            Notice?.Invoke($"{Settings.PasteLastHotkey} (paste last dictation) is already used by another app.", true);

        _hotkey.Unregister(AddWordHotkeyId);
        AddWordRegistered = Settings.AddWordEnabled && _hotkey.Register(AddWordHotkeyId, Settings.AddWordHotkey);
        if (Settings.AddWordEnabled && !AddWordRegistered && announceConflict)
            Notice?.Invoke($"{Settings.AddWordHotkey} (add a word to the dictionary) is already used by another app.", true);
    }

    /// <summary>Temporarily frees the hotkeys, only while a new shortcut is being recorded.</summary>
    public void SuspendHotkey()
    {
        _hotkeySuspensions++;
        _hotkey.Unregister(DictateHotkeyId);
        _hotkey.Unregister(PasteLastHotkeyId);
        _hotkey.Unregister(AddWordHotkeyId);
        HotkeyRegistered = PasteLastRegistered = AddWordRegistered = false;
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
        (HotkeyTarget Target, HotkeyBinding Binding, bool InUse, string Name)[] others =
        [
            (HotkeyTarget.Dictate, Settings.Hotkey, true, "dictation"),
            (HotkeyTarget.PasteLast, Settings.PasteLastHotkey, Settings.PasteLastEnabled, "paste-last"),
            (HotkeyTarget.AddWord, Settings.AddWordHotkey, Settings.AddWordEnabled, "add-a-word"),
        ];
        foreach (var o in others)
            if (o.Target != target && o.InUse && binding.SameAs(o.Binding))
                return $"{binding} is already your {o.Name} shortcut.";
        if (binding.Key == WinFormsKeys.Escape && !binding.Ctrl && !binding.Alt && !binding.Shift && !binding.Win)
            return "Esc is reserved for cancelling a dictation.";
        if (binding.Key == WinFormsKeys.MButton && !binding.Ctrl && !binding.Alt && !binding.Shift && !binding.Win)
            return "Middle click on its own is used everywhere. Hold Ctrl, Alt, Shift or Win with it, or use a side button.";
        if (!_hotkey.IsAvailable(binding))
            return $"{binding} is already used by another app. Try another combination.";

        Update(s =>
        {
            switch (target)
            {
                case HotkeyTarget.Dictate: s.Hotkey = binding; break;
                case HotkeyTarget.PasteLast: s.PasteLastHotkey = binding; break;
                default: s.AddWordHotkey = binding; break;
            }
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
            || before.PasteLastEnabled != Settings.PasteLastEnabled
            || !before.AddWordHotkey.SameAs(Settings.AddWordHotkey)
            || before.AddWordEnabled != Settings.AddWordEnabled)
        {
            RegisterHotkeys(announceConflict: true);
        }
        if (before.RecentLimit != Settings.RecentLimit) History.Trim(Settings.RecentLimit);
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
        // Captured now, while the target still has focus; its profile applies to this dictation.
        _target = ForegroundApp.Current();
        ActiveSettings = Settings.ForApp(_target?.ProcessName);
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
        _handsFree = Settings.Activation == ActivationMode.HandsFree;
        _session++;
        _chunks.Clear();
        _chunkFailed = false;
        _chunkChain = Task.CompletedTask;
        if (_handsFree) _chunkTimer.Start();
        State = DictationState.Recording;
        Sounds.Start();
        WakeModel(); // a sleeping model reloads while you talk
        _hotkey.Register(CancelHotkeyId, EscapeKey); // Esc cancels, only while listening
        // Hands-free sessions can run long; they're transcribed as they go, so only a generous safety limit applies.
        _maxLengthTimer.Interval = TimeSpan.FromMinutes(_handsFree ? 120 : Math.Max(1, Settings.MaxRecordingMinutes));
        _maxLengthTimer.Start();
        PreviewText = "";
        if (ActiveSettings.LivePreview)
        {
            _previewTimer.Interval = TimeSpan.FromSeconds(1);
            _previewTimer.Start();
        }
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
        _session++; // pieces still being transcribed are thrown away
        _chunks.Clear();
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
        _chunkTimer.Stop();
        StopPreview();
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
        var settings = ActiveSettings;
        string? raw;
        if (_handsFree)
        {
            // Wait for the pieces already sent off, then add the last one and treat it all as one dictation,
            // so styles, paragraphs and corrections see the whole thing.
            await _chunkChain;
            if (_chunkFailed) throw new InvalidOperationException("Part of the hands-free recording couldn't be transcribed.");
            string? last = await TranscribeRawAsync(samples, settings);
            var parts = _chunks.Append(last).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
            raw = parts.Count == 0 ? null : string.Join(" ", parts);
            LastAudioSeconds = (DateTime.Now - _recordingStarted).TotalSeconds;
        }
        else
        {
            raw = await TranscribeRawAsync(samples, settings);
        }
        if (raw == null) return new DictationOutcome(OutcomeKind.NoSpeech);
        string clean = TextCleaner.Process(raw, settings);
        if (clean.Length == 0) return new DictationOutcome(OutcomeKind.Empty);

        var item = Remember(raw, clean, _target, settings);
        Log.Write($"Dictation: {LastAudioSeconds:F1} s audio, {LastTranscribeSeconds:F2} s to transcribe, {item.WordsRemoved} words removed");

        var kind = await DeliverAsync(settings.AddTrailingSpace ? clean + " " : clean, clean, settings);
        return new DictationOutcome(kind, item);
    }

    /// <returns>Null when there was no speech in the recording.</returns>
    async Task<(string Raw, string Clean)?> TranscribeAsync(float[] samples, AppSettings settings)
    {
        string? raw = await TranscribeRawAsync(samples, settings);
        return raw == null ? null : (raw, TextCleaner.Process(raw, settings));
    }

    /// <returns>What the model heard, or null when there was no speech (including phrases it made up from noise).</returns>
    async Task<string?> TranscribeRawAsync(float[] samples, AppSettings settings)
    {
        var audio = AudioPrep.Prepare(samples);
        if (audio == null) return null;
        double loudSeconds = AudioPrep.LoudSeconds(samples);

        await EnsureModelReadyAsync();

        var timer = Stopwatch.StartNew();
        string raw = await Task.Run(() => _transcriber.TranscribeAsync(audio, settings, CancellationToken.None));
        LastTranscribeSeconds = timer.Elapsed.TotalSeconds;
        LastAudioSeconds = audio.Length / (double)AudioRecorder.SampleRate;
        _lastUsed = DateTime.Now;
        if (TextCleaner.IsLikelyPhantom(raw, loudSeconds))
        {
            Log.Write($"Ignored a phrase the speech model made up from background noise ({loudSeconds:F2} s of clear speech)");
            return null;
        }
        return raw;
    }

    // ---- hands-free ----

    /// <summary>
    /// While a hands-free session runs, sends each stretch of speech off to be transcribed once there's a
    /// pause, so a long session finishes almost as soon as it's turned off.
    /// </summary>
    void CutChunkAtPause()
    {
        if (!_handsFree || State != DictationState.Recording) return;
        int count = _recorder.Count;
        if (count < MinChunkSeconds * AudioRecorder.SampleRate) return;
        bool pause = _recorder.TailRms(0.7) < 0.008f;
        if (!pause && count < MaxChunkSeconds * AudioRecorder.SampleRate) return;

        var samples = _recorder.Take(count);
        Log.Write($"Hands-free: sent {count / (double)AudioRecorder.SampleRate:F1} s of speech off to be transcribed");
        var settings = ActiveSettings;
        int session = _session;
        var previous = _chunkChain;
        _chunkChain = TranscribeChunkAsync(previous, samples, settings, session);
    }

    async Task TranscribeChunkAsync(Task previous, float[] samples, AppSettings settings, int session)
    {
        await previous; // pieces are added in the order they were spoken
        try
        {
            string? raw = await TranscribeRawAsync(samples, settings);
            if (session != _session || raw == null) return;
            _chunks.Add(raw);
            if (ActiveSettings.LivePreview && State == DictationState.Recording)
            {
                PreviewText = TextCleaner.Process(string.Join(" ", _chunks), settings).Replace("\n", " ");
                PreviewChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // The whole session is still on disk, so a retry from Home can recover it.
            Log.Write($"Hands-free piece failed: {ex.Message}");
            if (session == _session) _chunkFailed = true;
        }
    }

    /// <summary>Types or pastes the text, falling back to the clipboard where typing can't reach.</summary>
    /// <summary>Adds a dictation to history, or in private mode only to the totals (kept in memory for paste-last).</summary>
    RecentDictation Remember(string raw, string clean, AppInfo? app, AppSettings settings)
    {
        string? language = settings.Language == "auto" ? _transcriber.LastLanguage : null;
        if (Settings.PrivateMode) return _lastPrivate = History.CountOnly(raw, clean, app, language);
        _lastPrivate = null;
        return History.Add(raw, clean, app, Settings.RecentLimit, language);
    }

    async Task<OutcomeKind> DeliverAsync(string text, string clipboardText, AppSettings settings)
    {
        if (settings.JoinLines)
        {
            // Terminals can run each line of a multi-line paste as its own command.
            text = Regex.Replace(text, @"[ \t]*(?:\r?\n)+[ \t]*", " ");
            clipboardText = Regex.Replace(clipboardText, @"[ \t]*(?:\r?\n)+[ \t]*", " ");
        }
        // Started from BetterSTT's own window (or it has focus): there is nothing to paste into.
        // Test runs (--no-paste) never type into other windows either.
        if (TextInjector.ClipboardOnly || TextInjector.IsOwnWindowFocused())
        {
            TextInjector.SetClipboard(clipboardText);
            return OutcomeKind.Copied;
        }
        if (TextInjector.IsForegroundBlocked())
        {
            TextInjector.SetClipboard(clipboardText);
            if (!settings.ShowOverlay)
                Notice?.Invoke("That window is running as administrator, so it blocks typing from other apps. Your text is on the clipboard; press Ctrl+V.", false);
            return OutcomeKind.Blocked;
        }
        await TextInjector.InjectAsync(text, settings);
        return OutcomeKind.Pasted;
    }

    // ---- add a word ----

    /// <summary>Copies what's selected in the focused app and asks the UI to offer adding it to the dictionary.</summary>
    async Task RequestAddWordAsync()
    {
        string? selected = TextInjector.IsOwnWindowFocused() ? null : await TextInjector.CopySelectionAsync();
        AddWordRequested?.Invoke(selected);
    }

    /// <summary>Private mode on or off: while on, new dictations aren't kept in the recent list.</summary>
    public void SetPrivateMode(bool on) => Update(s => s.PrivateMode = on);

    /// <summary>Makes a style the usual one.</summary>
    public void SetStyle(string name) => Update(s => s.Style = name);

    // ---- live preview ----

    /// <summary>
    /// While recording, re-transcribes the last few seconds so the indicator can show a draft of your
    /// words. It only runs when the model is idle, backs off on slower machines, and is cancelled the
    /// moment you stop, so the final transcription is never held up by more than a fraction of a second.
    /// </summary>
    async Task UpdatePreviewAsync()
    {
        if (_previewBusy || State != DictationState.Recording || ModelState != ModelState.Ready) return;
        var samples = _recorder.Snapshot(maxSeconds: 25);
        if (samples.Length < AudioRecorder.SampleRate) return;
        var audio = AudioPrep.Prepare(samples);
        if (audio == null) return;

        _previewBusy = true;
        var ct = _previewCts.Token;
        var settings = ActiveSettings;
        var timer = Stopwatch.StartNew();
        try
        {
            string raw = await Task.Run(() => _transcriber.TranscribeAsync(audio, settings, ct), ct);
            if (ct.IsCancellationRequested || State != DictationState.Recording) return;
            // In hands-free, the draft is what's been transcribed so far plus the words since the last pause.
            string sofar = _handsFree ? string.Join(" ", _chunks) + " " : "";
            PreviewText = TextCleaner.Process(sofar + raw, settings).Replace("\n", " ");
            PreviewChanged?.Invoke();
            // Refresh about twice as often as a preview takes, within 0.8–4 s.
            _previewTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(timer.ElapsedMilliseconds * 2, 800, 4000));
        }
        catch (OperationCanceledException)
        {
            // Stopped or cancelled mid-preview.
        }
        catch (Exception ex)
        {
            Log.Write($"Live preview failed: {ex.Message}");
        }
        finally
        {
            _previewBusy = false;
        }
    }

    void StopPreview()
    {
        _previewTimer.Stop();
        _previewCts.Cancel();
        _previewCts = new CancellationTokenSource();
    }

    /// <summary>Shows an informational message as a tray notification.</summary>
    public void Announce(string message) => Notice?.Invoke(message, false);

    // ---- updates ----

    /// <summary>A newer version, downloaded and ready to install.</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }
    public bool CheckingForUpdates { get; private set; }
    /// <summary>Why the last check failed, or null.</summary>
    public string? UpdateError { get; private set; }
    public event Action? UpdateChanged;

    /// <summary>For screenshots: shows the update banner for a made-up version.</summary>
    public void ShowSampleUpdate()
    {
        AvailableUpdate = new UpdateInfo(new Version(Updater.Current.Major, Updater.Current.Minor, Updater.Current.Build + 1), "");
        UpdateChanged?.Invoke();
    }

    /// <summary>Looks for a new release and downloads it in the background.</summary>
    public async Task CheckForUpdatesAsync()
    {
        if (CheckingForUpdates) return;
        CheckingForUpdates = true;
        UpdateError = null;
        UpdateChanged?.Invoke();
        try
        {
            var found = await Task.Run(() => Updater.CheckAsync(CancellationToken.None));
            bool isNew = found != null && found.Version != AvailableUpdate?.Version;
            AvailableUpdate = found;
            // Only a completed check counts; after a failure (e.g. offline) it tries again within the hour.
            Update(s => s.LastUpdateCheck = DateTime.Now);
            if (isNew)
                Notice?.Invoke($"BetterSTT {found!.Version} is ready. It installs the next time you open BetterSTT, or choose Install now in its window.", false);
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed: {ex.Message}");
            UpdateError = ex is HttpRequestException ? "Couldn't reach GitHub. Check your internet connection." : ex.Message;
        }
        finally
        {
            CheckingForUpdates = false;
            UpdateChanged?.Invoke();
        }
    }

    // ---- paste last / recovery ----

    public async Task PasteLastAsync()
    {
        var last = LastDictation;
        if (State != DictationState.Idle || last == null)
        {
            Sounds.Error();
            return;
        }

        _target = ForegroundApp.Current();
        ActiveSettings = Settings.ForApp(_target?.ProcessName);
        var settings = ActiveSettings;
        var kind = await DeliverAsync(settings.AddTrailingSpace ? last.Clean + " " : last.Clean, last.Clean, settings);
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
        // The app a saved recording was meant for is unknown, so the general settings apply.
        _target = null;
        ActiveSettings = Settings.Clone();
        State = DictationState.Transcribing;
        StateChanged?.Invoke();

        DictationOutcome outcome;
        try
        {
            var result = await TranscribeAsync(PendingAudio.Read(path), ActiveSettings);
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
                var item = Remember(result.Value.Raw, result.Value.Clean, null, ActiveSettings);
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
        _updateTimer.Stop();
        _hotkey.Dispose();
        _recorder.Dispose();
        _transcriber.Dispose();
    }
}
