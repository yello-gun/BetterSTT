using BetterSTT;

namespace BetterSTT.Tests;

public class SpokenLayoutTests
{
    static string Natural(string s) => TextCleaner.Process(s, new CleanupOptions(), [], WritingStyle.Natural());

    [Theory]
    [InlineData("Hi Sarah, new paragraph, I wanted to ask about Tuesday.", "Hi Sarah,\n\nI wanted to ask about Tuesday.")]
    [InlineData("Buy milk. New line. Buy eggs. New line. Buy bread.", "Buy milk.\nBuy eggs.\nBuy bread.")]
    [InlineData("First point is done, new paragraph, second point is late.", "First point is done.\n\nSecond point is late.")]
    [InlineData("That's all. New paragraph.", "That's all.")]
    public void Spoken_breaks_start_lines_and_paragraphs(string spoken, string expected) =>
        Assert.Equal(expected, Natural(spoken));

    [Theory]
    [InlineData("Add a new line to the config file.")]
    [InlineData("We need a new paragraph about pricing.")]
    public void The_words_themselves_are_left_alone(string spoken) =>
        Assert.Equal(spoken, Natural(spoken));

    [Fact]
    public void Formal_paragraphs_work_inside_spoken_blocks()
    {
        string text = TextCleaner.Process("Hi Sarah, new paragraph, I checked the numbers. Also, the deck is ready. Thanks.",
            new CleanupOptions(), [], WritingStyle.Formal());
        Assert.Equal("Hi Sarah,\n\nI checked the numbers.\n\nAlso, the deck is ready.\n\nThanks.", text);
    }

    [Fact]
    public void Can_be_switched_off()
    {
        var style = WritingStyle.Natural();
        style.SpokenLayout = false;
        Assert.Equal("Buy milk. New line. Buy eggs.", TextCleaner.Process("Buy milk. New line. Buy eggs.", new CleanupOptions(), [], style));
    }
}

public class SpokenPunctuationTests
{
    [Theory]
    [InlineData("Hello comma how are you question mark", "Hello, how are you?")]
    [InlineData("Hello, comma, how are you? Question mark.", "Hello, how are you?")]
    [InlineData("It works exclamation point I'm so happy period", "It works! I'm so happy.")]
    [InlineData("He said open quote hello close quote and left period", "He said “hello” and left.")]
    [InlineData("Note colon bring snacks semicolon drinks too period", "Note: bring snacks; drinks too.")]
    public void Spoken_marks_become_punctuation(string spoken, string expected) =>
        Assert.Equal(expected, TextCleaner.ApplySpokenPunctuation(spoken));

    [Fact]
    public void Period_as_a_word_is_kept_mid_sentence() =>
        Assert.Equal("The trial period lasts a week.", TextCleaner.ApplySpokenPunctuation("The trial period lasts a week."));
}

public class PhantomTests
{
    [Theory]
    [InlineData("Thank you.", 0.1, true)]
    [InlineData(" Thanks for watching!", 0.0, true)]
    [InlineData("[BLANK_AUDIO]", 0.0, true)]
    [InlineData("Thank you.", 0.8, false)]          // said clearly: kept
    [InlineData("Thank you for the update.", 0.1, false)]
    public void Invented_phrases_from_quiet_audio_are_dropped(string raw, double loudSeconds, bool phantom) =>
        Assert.Equal(phantom, TextCleaner.IsLikelyPhantom(raw, loudSeconds));

    [Fact]
    public void Loud_seconds_counts_only_clear_speech()
    {
        var quiet = Enumerable.Repeat(0.005f, 16000).ToArray();
        var loud = Enumerable.Range(0, 16000).Select(i => (float)(0.2 * Math.Sin(i / 5.0))).ToArray();
        Assert.Equal(0, AudioPrep.LoudSeconds(quiet), 2);
        Assert.Equal(1, AudioPrep.LoudSeconds(loud), 1);
    }
}
