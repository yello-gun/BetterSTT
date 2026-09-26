using BetterSTT;

namespace BetterSTT.Tests;

public class TextCleanerTests
{
    static readonly CleanupOptions Strict = new();

    [Theory]
    // Filler sounds
    [InlineData("Um, I think we should go.", "I think we should go.")]
    [InlineData("I think, um, we should go.", "I think we should go.")]
    [InlineData("I think, ummm, we should go.", "I think we should go.")]
    [InlineData("Hmm. That's interesting.", "That's interesting.")]
    [InlineData("That was great. Um, and then we left.", "That was great. And then we left.")]
    [InlineData("We should go, uh.", "We should go.")]
    [InlineData("Um, uh, so, what do we do?", "What do we do?")]
    // Pause ellipses
    [InlineData("So I was thinking... maybe we could try it.", "So I was thinking maybe we could try it.")]
    [InlineData("Let me check...", "Let me check.")]
    [InlineData("I was thinking… We should leave.", "I was thinking. We should leave.")]
    [InlineData("...and then it broke.", "And then it broke.")]
    [InlineData("Well, uh... I don't know.", "I don't know.")]
    [InlineData("I was thinking... like, maybe we could.", "I was thinking maybe we could.")]
    [InlineData("That was great. Um... so we left.", "That was great. So we left.")]
    [InlineData("We could, uh... maybe wait.", "We could maybe wait.")]
    [InlineData("Wait... what?", "Wait what?")]
    [InlineData("Um, so I was thinking... like, maybe we could, uh, you know, try the the new approach?",
                "So I was thinking maybe we could try the new approach?")]
    // Verbal fillers only when they stand alone
    [InlineData("It was, like, really big.", "It was really big.")]
    [InlineData("You know, I think it works.", "I think it works.")]
    [InlineData("It works, you know.", "It works.")]
    [InlineData("Um, like, I don't know.", "I don't know.")]
    [InlineData("I like pizza.", "I like pizza.")]
    [InlineData("I was like, wow.", "I was like, wow.")]
    [InlineData("You know the answer.", "You know the answer.")]
    [InlineData("It went well, and we shipped it.", "It went well, and we shipped it.")]
    // Stutters
    [InlineData("I-I think the the plan is good.", "I think the plan is good.")]
    [InlineData("I, I, I don't know.", "I don't know.")]
    [InlineData("I had had enough.", "I had had enough.")]
    // Words that merely contain filler letters are untouched
    [InlineData("Hummus and umbrellas are great.", "Hummus and umbrellas are great.")]
    [InlineData("Uh-huh, that's right.", "Uh-huh, that's right.")]
    // Non-speech tags
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("(upbeat music) Hello there.", "Hello there.")]
    [InlineData("", "")]
    public void Strict_mode(string input, string expected) =>
        Assert.Equal(expected, TextCleaner.Clean(input, Strict));

    [Fact]
    public void Disabled_leaves_text_alone()
    {
        var o = new CleanupOptions { Enabled = false };
        Assert.Equal("Um, I think... like, yes.", TextCleaner.Clean("Um,  I think... like, yes.", o));
    }

    [Fact]
    public void Each_rule_can_be_switched_off()
    {
        var o = new CleanupOptions { RemoveFillerSounds = false, RemoveDiscourseMarkers = false };
        Assert.Equal("Um, I think like, yes.", TextCleaner.Clean("Um, I think... like, yes.", o));
    }

    [Fact]
    public void Custom_filler_words_are_used()
    {
        var o = new CleanupOptions { FillerSounds = ["ehm"], DiscourseMarkers = ["okay so"] };
        Assert.Equal("We go now.", TextCleaner.Clean("Okay so, ehm, we go now.", o));
    }

    [Fact]
    public void Counts_words()
    {
        Assert.Equal(4, TextCleaner.CountWords("I don't know, um."));
    }
}
