using BetterSTT;
using NAudio.Wave;

namespace BetterSTT.Tests;

public class PendingAudioTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "BetterSTT.Tests." + Guid.NewGuid().ToString("N"));

    public PendingAudioTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static short[] Tone(int count) =>
        Enumerable.Range(0, count).Select(i => (short)(Math.Sin(i * 0.05) * 12000)).ToArray();

    [Fact]
    public void Reads_a_finished_recording()
    {
        string path = Path.Combine(_dir, "done.wav");
        var tone = Tone(16000);
        using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
            foreach (var s in tone) writer.WriteSample(s / 32768f);

        var samples = PendingAudio.Read(path);

        Assert.Equal(tone.Length, samples.Length);
        Assert.Equal(tone[123] / 32768f, samples[123], 3);
    }

    [Fact]
    public void Reads_a_recording_cut_off_by_a_crash()
    {
        // A crash leaves the header's size fields at whatever the last flush wrote (here: zero),
        // but the audio after them is still on disk and must not be lost.
        string path = Path.Combine(_dir, "crashed.wav");
        var tone = Tone(8000);
        using (var file = File.Create(path))
        using (var w = new BinaryWriter(file))
        {
            w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(0);
            foreach (var s in tone) w.Write(s);
        }

        var samples = PendingAudio.Read(path);

        Assert.Equal(tone.Length, samples.Length);
    }

    [Fact]
    public void Rejects_a_file_without_audio()
    {
        string path = Path.Combine(_dir, "empty.wav");
        File.WriteAllBytes(path, "RIFF\0\0\0\0WAVE"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => PendingAudio.Read(path));
    }
}
