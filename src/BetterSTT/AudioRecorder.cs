using NAudio.Wave;

namespace BetterSTT;

/// <summary>Records 16 kHz mono audio (the format Whisper expects) from a microphone.</summary>
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = 16000;

    readonly List<float> _samples = new();
    readonly object _gate = new();
    WaveInEvent? _wave;
    TaskCompletionSource? _stopped;
    WaveFileWriter? _spool;
    int _buffersSinceFlush;

    /// <summary>Peak level of the latest buffer, 0..1. Raised on a background thread.</summary>
    public event Action<float>? LevelChanged;

    public bool IsRecording => _wave != null || _testFeed != null;

    /// <summary>
    /// Automated tests only (--no-paste --test-audio file.wav): plays a 16 kHz mono WAV in real time instead
    /// of the microphone, followed by silence, so recordings are repeatable.
    /// </summary>
    public static string? TestInput { get; set; }
    CancellationTokenSource? _testFeed;

    void StartTestFeed(string path)
    {
        var cts = _testFeed = new CancellationTokenSource();
        var audio = PendingAudio.Read(path);
        _ = Task.Run(async () =>
        {
            const int chunk = SampleRate / 20; // 50 ms, like the microphone
            var buffer = new byte[chunk * 2];
            for (int pos = 0; !cts.IsCancellationRequested; pos += chunk)
            {
                for (int i = 0; i < chunk; i++)
                {
                    float v = pos + i < audio.Length ? audio[pos + i] : 0f;
                    short s = (short)Math.Clamp(v * 32768f, short.MinValue, short.MaxValue);
                    buffer[i * 2] = (byte)s;
                    buffer[i * 2 + 1] = (byte)(s >> 8);
                }
                OnData(this, new WaveInEventArgs(buffer, buffer.Length));
                try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { break; }
            }
        });
    }

    public static IReadOnlyList<string> GetDevices() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => WaveInEvent.GetCapabilities(i).ProductName).ToList();

    /// <param name="spoolPath">
    /// If given, the audio is also written to this WAV file as it arrives, so it survives a crash.
    /// </param>
    public void Start(int deviceNumber, string? spoolPath = null)
    {
        if (_wave != null || _testFeed != null) return;
        lock (_gate)
        {
            _samples.Clear();
            _spool = null;
            if (spoolPath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(spoolPath)!);
                    _spool = new WaveFileWriter(spoolPath, new WaveFormat(SampleRate, 16, 1));
                    _buffersSinceFlush = 0;
                }
                catch (Exception ex)
                {
                    Log.Write($"Could not save the recording to disk, continuing in memory only: {ex.Message}");
                }
            }
        }

        if (TestInput != null)
        {
            StartTestFeed(TestInput);
            return;
        }
        if (deviceNumber >= WaveInEvent.DeviceCount) deviceNumber = -1;
        var wave = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        wave.DataAvailable += OnData;
        wave.RecordingStopped += (_, _) => _stopped?.TrySetResult();
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wave.StartRecording();
        _wave = wave;
    }

    /// <summary>A copy of the most recent audio (up to <paramref name="maxSeconds"/>), without stopping.</summary>
    public float[] Snapshot(int maxSeconds)
    {
        lock (_gate)
        {
            int count = Math.Min(_samples.Count, maxSeconds * SampleRate);
            return _samples.GetRange(_samples.Count - count, count).ToArray();
        }
    }

    /// <summary>Samples held in memory (everything since the start, or since the last <see cref="Take"/>).</summary>
    public int Count
    {
        get { lock (_gate) return _samples.Count; }
    }

    /// <summary>
    /// Removes and returns the oldest <paramref name="count"/> samples while recording carries on (hands-free
    /// mode transcribes a long session piece by piece). The copy on disk keeps everything.
    /// </summary>
    public float[] Take(int count)
    {
        lock (_gate)
        {
            count = Math.Min(count, _samples.Count);
            var taken = _samples.GetRange(0, count).ToArray();
            _samples.RemoveRange(0, count);
            return taken;
        }
    }

    /// <summary>Loudness (RMS) of the last <paramref name="seconds"/> of audio.</summary>
    public float TailRms(double seconds)
    {
        lock (_gate)
        {
            int n = Math.Min(_samples.Count, (int)(seconds * SampleRate));
            if (n == 0) return 0;
            double sum = 0;
            for (int i = _samples.Count - n; i < _samples.Count; i++) sum += _samples[i] * _samples[i];
            return (float)Math.Sqrt(sum / n);
        }
    }

    public async Task<float[]> StopAsync()
    {
        if (_testFeed != null)
        {
            _testFeed.Cancel();
            _testFeed = null;
            await Task.Delay(100);
            lock (_gate)
            {
                _spool?.Dispose();
                _spool = null;
                return _samples.ToArray();
            }
        }
        var wave = _wave;
        if (wave == null) return [];
        _wave = null;
        wave.StopRecording();
        // Wait for the final buffers to flush (with a safety timeout).
        if (_stopped != null) await Task.WhenAny(_stopped.Task, Task.Delay(1000));
        wave.Dispose();
        lock (_gate)
        {
            _spool?.Dispose(); // finalizes the WAV header
            _spool = null;
            return _samples.ToArray();
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        float peak = 0;
        lock (_gate)
        {
            if (_spool != null)
            {
                try
                {
                    _spool.Write(e.Buffer, 0, e.BytesRecorded);
                    // Flushing rewrites the header sizes; about once a second keeps a crash copy usable.
                    if (++_buffersSinceFlush >= 20)
                    {
                        _spool.Flush();
                        _buffersSinceFlush = 0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"Writing the recording to disk failed: {ex.Message}");
                    _spool.Dispose();
                    _spool = null;
                }
            }
            for (int i = 0; i < count; i++)
            {
                float v = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                _samples.Add(v);
                float a = Math.Abs(v);
                if (a > peak) peak = a;
            }
        }
        LevelChanged?.Invoke(peak);
    }

    public void Dispose()
    {
        _wave?.Dispose();
        _wave = null;
        lock (_gate)
        {
            _spool?.Dispose();
            _spool = null;
        }
    }
}

/// <summary>
/// Recordings waiting to be transcribed. Each dictation is written here while it's recorded and
/// deleted once transcribed, so a crash or failed transcription never loses what was said.
/// </summary>
public static class PendingAudio
{
    public static string NewPath() =>
        Path.Combine(AppPaths.Pending, $"dictation-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");

    /// <summary>Oldest first. <paramref name="except"/> skips the recording in progress.</summary>
    public static IReadOnlyList<string> List(string? except = null)
    {
        if (!Directory.Exists(AppPaths.Pending)) return [];
        return Directory.GetFiles(AppPaths.Pending, "dictation-*.wav")
            .Where(p => !string.Equals(p, except, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    public static DateTime RecordedAt(string path) => File.GetCreationTime(path);

    public static void Delete(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); }
        catch (Exception ex) { Log.Write($"Could not delete {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>
    /// Reads a 16-bit mono recording. The data chunk is read to the end of the file rather than
    /// trusting its size field, which is stale if the app crashed mid-recording.
    /// </summary>
    public static float[] Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int pos = 12; // after "RIFF" <size> "WAVE"
        while (pos + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data")
            {
                int start = pos + 8;
                int count = (bytes.Length - start) / 2;
                var samples = new float[count];
                for (int i = 0; i < count; i++) samples[i] = BitConverter.ToInt16(bytes, start + i * 2) / 32768f;
                return samples;
            }
            if (size < 0) break;
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException($"{Path.GetFileName(path)} has no audio data.");
    }
}

/// <summary>Opens a microphone only to report its level (for the settings meter); records nothing.</summary>
public sealed class LevelMonitor : IDisposable
{
    WaveInEvent? _wave;

    /// <summary>Peak level 0..1, raised on a background thread.</summary>
    public event Action<float>? LevelChanged;

    public void Start(int deviceNumber)
    {
        Stop();
        try
        {
            if (deviceNumber >= WaveInEvent.DeviceCount) deviceNumber = -1;
            _wave = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(AudioRecorder.SampleRate, 16, 1),
                BufferMilliseconds = 60,
            };
            _wave.DataAvailable += (_, e) =>
            {
                float peak = 0;
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
                LevelChanged?.Invoke(peak);
            };
            _wave.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Write($"Level monitor could not open the microphone: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        if (_wave == null) return;
        try { _wave.StopRecording(); } catch { /* device already gone */ }
        _wave.Dispose();
        _wave = null;
    }

    public void Dispose() => Stop();
}

public static class AudioPrep
{
    const int FrameSize = AudioRecorder.SampleRate / 50; // 20 ms
    const float SpeechRms = 0.012f;                      // ~ -38 dBFS
    const int PadSamples = AudioRecorder.SampleRate * 3 / 10;

    /// <summary>How many seconds of the audio are clearly spoken (louder than background noise), before any gain.</summary>
    public static double LoudSeconds(float[] samples)
    {
        const float loud = 0.02f; // ~ -34 dBFS: a voice close to the microphone, not a room in the background
        int frames = samples.Length / FrameSize, count = 0;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * FrameSize; i < (f + 1) * FrameSize; i++) sum += samples[i] * samples[i];
            if (Math.Sqrt(sum / FrameSize) >= loud) count++;
        }
        return count * FrameSize / (double)AudioRecorder.SampleRate;
    }

    /// <summary>
    /// Trims silence, normalizes quiet input and pads to Whisper's 1 s minimum.
    /// Returns null when there is no speech at all, so silence never reaches Whisper
    /// (which tends to hallucinate "Thank you." on empty audio).
    /// </summary>
    public static float[]? Prepare(float[] samples)
    {
        int frames = samples.Length / FrameSize;
        int first = -1, last = -1;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * FrameSize; i < (f + 1) * FrameSize; i++) sum += samples[i] * samples[i];
            if (Math.Sqrt(sum / FrameSize) >= SpeechRms)
            {
                if (first < 0) first = f;
                last = f;
            }
        }
        if (first < 0 || last - first < 4) return null; // under ~100 ms of sound

        int start = Math.Max(0, first * FrameSize - PadSamples);
        int end = Math.Min(samples.Length, (last + 1) * FrameSize + PadSamples);
        var trimmed = samples[start..end];

        float peak = trimmed.Max(Math.Abs);
        if (peak is > 0 and < 0.5f)
        {
            float gain = Math.Min(0.5f / peak, 10f);
            for (int i = 0; i < trimmed.Length; i++) trimmed[i] *= gain;
        }

        int minLength = AudioRecorder.SampleRate * 11 / 10;
        if (trimmed.Length < minLength) Array.Resize(ref trimmed, minLength);
        return trimmed;
    }
}
