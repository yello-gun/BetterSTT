using BetterSTT;

namespace BetterSTT.Tests;

public class TextDiffTests
{
    static string Removed(string raw) =>
        string.Join("|", TextDiff.Compare(raw, TextCleaner.Clean(raw, new CleanupOptions()))
            .Where(p => p.Removed).Select(p => p.Text.Trim()));

    static string Rebuilt(string raw) =>
        string.Concat(TextDiff.Compare(raw, TextCleaner.Clean(raw, new CleanupOptions())).Select(p => p.Text));

    [Fact]
    public void Marks_fillers_pauses_and_repeats()
    {
        const string raw = "Um, so I was thinking... like, maybe we could, uh, you know, try the the new approach?";
        // Adjacent removals merge into one struck-out span.
        Assert.Equal("Um,|... like,|, uh, you know,|the", Removed(raw));
    }

    [Fact]
    public void Parts_rebuild_the_original_text_exactly()
    {
        const string raw = "  Hmm. Can you, you know, rewrite this?";
        Assert.Equal(raw, Rebuilt(raw));
    }

    [Fact]
    public void Nothing_removed_when_text_is_already_clean()
    {
        Assert.Equal("", Removed("I like pizza, and I had had enough."));
    }
}
