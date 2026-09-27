using BetterSTT;

namespace BetterSTT.Tests;

public class CorrectionTests
{
    static string Clean(string s) => TextCleaner.Clean(s, new CleanupOptions());

    [Theory]
    // A word of the same kind is swapped, and the rest of the sentence is kept.
    [InlineData("Let's meet on Tuesday, no wait, Wednesday.", "Let's meet on Wednesday.")]
    [InlineData("Let's meet Tuesday at 3, no wait, Wednesday.", "Let's meet Wednesday at 3.")]
    [InlineData("Let's meet on Tuesday, no wait, Wednesday, to go over the plan.", "Let's meet on Wednesday to go over the plan.")]
    [InlineData("We need three, no wait, four copies.", "We need four copies.")]
    [InlineData("Send it to John, sorry, I mean Sarah.", "Send it to Sarah.")]
    [InlineData("The meeting is at 3 pm. No, wait. 4 pm.", "The meeting is at 4 pm.")]
    [InlineData("Paint it red, or rather blue.", "Paint it blue.")]
    // A repeated word shows where the restart begins.
    [InlineData("Put the book on the table, no wait, the shelf.", "Put the book on the shelf.")]
    [InlineData("Send it to marketing, no wait, to sales.", "Send it to sales.")]
    // A restarted clause replaces the clause.
    [InlineData("I think we should, no wait, let's just cancel it.", "Let's just cancel it.")]
    [InlineData("Let's go to the park. Scratch that. Let's stay home.", "Let's stay home.")]
    [InlineData("We'll ship it Friday, and we can test it Monday, no wait, we can test it Thursday.", "We'll ship it Friday, and we can test it Thursday.")]
    // Otherwise the last few words are replaced.
    [InlineData("I want pizza, no wait, a burger.", "I want a burger.")]
    [InlineData("The file is big, no wait, huge.", "The file is huge.")]
    // "Scratch that" on its own drops what came before it.
    [InlineData("Buy milk. Scratch that. Buy eggs and bread.", "Buy eggs and bread.")]
    [InlineData("First point is done. The second one is late, scratch that.", "First point is done.")]
    // Fillers around the correction are ignored.
    [InlineData("Um, it's on Tuesday, uh, no wait, um, Wednesday.", "It's on Wednesday.")]
    public void Corrections_are_worked_out_from_context(string spoken, string expected) =>
        Assert.Equal(expected, Clean(spoken));

    [Theory]
    [InlineData("I'll be late, sorry, traffic is bad.")]
    [InlineData("Sorry, I can't make it.")]
    [InlineData("There's no wait time at all.")]
    [InlineData("Actually, I think it works.")]
    [InlineData("It's fine. Actually, I think it works.")]
    [InlineData("Hold on, wait, the door is open.")]
    [InlineData("I read the book, sorry, the traffic was bad.")]
    public void Ordinary_speech_is_left_alone(string spoken) =>
        // The correction step changes nothing (other rules, like dropping "Actually,", still apply).
        Assert.Equal(TextCleaner.Clean(spoken, new CleanupOptions { FixCorrections = false }), Clean(spoken));

    [Fact]
    public void Can_be_switched_off() =>
        Assert.Equal("Let's meet on Tuesday, no wait, Wednesday.",
            TextCleaner.Clean("Let's meet on Tuesday, no wait, Wednesday.", new CleanupOptions { FixCorrections = false }));
}
