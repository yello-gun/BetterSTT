using BetterSTT;

namespace BetterSTT.Tests;

public class HistoryTests
{
    static HistoryStore Store() => new() { Persist = false };

    [Theory]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(15, 12)]
    [InlineData(0, 12)]
    public void Keeps_the_chosen_number(int limit, int expected)
    {
        var h = Store();
        for (int i = 0; i < 12; i++) h.Add($"um, dictation {i}", $"Dictation {i}", null, limit);
        Assert.Equal(expected, h.Recent.Count);
        Assert.Equal("Dictation 11", h.Recent[0].Clean); // newest first
        Assert.Equal(12, h.Dictations);                   // totals count every dictation
    }

    [Fact]
    public void Lowering_the_limit_drops_the_oldest()
    {
        var h = Store();
        for (int i = 0; i < 10; i++) h.Add("raw", $"Dictation {i}", null, 0);
        h.Trim(3);
        Assert.Equal(["Dictation 9", "Dictation 8", "Dictation 7"], h.Recent.Select(r => r.Clean));
    }

    [Fact]
    public void Unlimited_never_trims()
    {
        var h = Store();
        for (int i = 0; i < 40; i++) h.Add("raw", "clean", null, 0);
        h.Trim(0);
        Assert.Equal(40, h.Recent.Count);
    }
}
