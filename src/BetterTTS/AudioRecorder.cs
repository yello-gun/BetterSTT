using NAudio.Wave;

namespace BetterTTS;

/// <summary>Records 16 kHz mono audio (the format Whisper expects) from a microphone.</summary>
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = 16000;

    readonly List<float> _samples = new();
    readonly object _gate = new();
    WaveInEvent? _wave;
    TaskCompletionSource? _stopped;

    /// <summary>Peak level of the latest buffer, 0..1. Raised on a background thread.</summary>
    public event Action<float>? LevelChanged;

    public bool IsRecording => _wave != null;

    public static IReadOnlyList<string> GetDevices() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => WaveInEvent.GetCapabilities(i).ProductName).ToList();

    public void Start(int deviceNumber)
    {
        if (_wave != null) return;
        lock (_gate) _samples.Clear();

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

    public async Task<float[]> StopAsync()
    {
        var wave = _wave;
        if (wave == null) return [];
        _wave = null;
        wave.StopRecording();
        // Wait for the final buffers to flush (with a safety timeout).
        if (_stopped != null) await Task.WhenAny(_stopped.Task, Task.Delay(1000));
        wave.Dispose();
        lock (_gate) return _samples.ToArray();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        float peak = 0;
        lock (_gate)
        {
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
