using System.Text;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace BetterSTT;

/// <summary>Owns the local Whisper model: downloads it on first use, loads it, and transcribes audio.</summary>
public sealed class Transcriber : IDisposable
{
    WhisperFactory? _factory;
    string? _loadedPath;
    // Loading a model disposes the old one, so it must never overlap a transcription.
    readonly SemaphoreSlim _gate = new(1, 1);

    public string RuntimeName => RuntimeOptions.LoadedLibrary?.ToString() ?? "not loaded";

    public static string ModelPath(GgmlType type, QuantizationType quant) =>
        Path.Combine(AppPaths.Models, $"ggml-{type}-{quant}.bin".ToLowerInvariant());

    public static bool IsDownloaded(AppSettings s) => File.Exists(ModelPath(s.ModelType, s.ModelQuantization));

    public static bool IsDownloaded(ModelOption m) => File.Exists(ModelPath(m.Type, m.Quantization));

    /// <summary>Downloads the model if missing. Progress reports megabytes received.</summary>
    public static async Task EnsureModelAsync(AppSettings s, IProgress<long>? progressMb, CancellationToken ct)
    {
        string path = ModelPath(s.ModelType, s.ModelQuantization);
        if (File.Exists(path)) return;

        Directory.CreateDirectory(AppPaths.Models);
        string partial = path + ".part";
        Log.Write($"Downloading model {s.ModelType} {s.ModelQuantization}");
        await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(s.ModelType, s.ModelQuantization, ct))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long total = 0, lastMb = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                long mb = total >> 20;
                if (mb != lastMb) progressMb?.Report(lastMb = mb);
            }
        }
        File.Move(partial, path, overwrite: true);
        Log.Write("Model download complete");
    }

    public void Load(AppSettings s)
    {
        string path = ModelPath(s.ModelType, s.ModelQuantization);
        _gate.Wait();
        try
        {
            if (_factory != null && _loadedPath == path) return;

            _factory?.Dispose();
            _factory = null;
            _factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = s.UseGpu });
            _loadedPath = path;
            Log.Write($"Loaded {Path.GetFileName(path)} using runtime: {RuntimeName}");

            // Run one tiny inference so GPU kernels are compiled before the first real dictation.
            try
            {
                RunAsync(_factory, new float[AudioRecorder.SampleRate], s, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Write($"Warm-up failed: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Frees the model's memory. The next <see cref="Load"/> brings it back.</summary>
    public void Unload()
    {
        _gate.Wait();
        try
        {
            _factory?.Dispose();
            _factory = null;
            _loadedPath = null;
        }
        finally
        {
            _gate.Release();
        }
        Log.Write("Unloaded the speech model to free memory");
    }

    public async Task<string> TranscribeAsync(float[] samples, AppSettings s, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var factory = _factory ?? throw new InvalidOperationException("The speech model is not loaded yet.");
            return await RunAsync(factory, samples, s, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<string> RunAsync(WhisperFactory factory, float[] samples, AppSettings s, CancellationToken ct)
    {
        var builder = factory.CreateBuilder()
            .WithNoSpeechThreshold(0.6f)
            .WithTemperature(0f)
            .WithPrompt(s.BuildPrompt());
        builder = string.IsNullOrWhiteSpace(s.Language) || s.Language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(s.Language.Trim());

        using var processor = builder.Build();
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
            text.Append(segment.Text);
        return text.ToString().Trim();
    }

    public void Dispose()
    {
        _gate.Wait();
        _factory?.Dispose();
        _factory = null;
        _gate.Release();
    }
}
