using BetterSTT;

namespace BetterSTT.Tests;

public class RepeatTests
{
    static string Clean(string s) => TextCleaner.Clean(s, new CleanupOptions());

    [Theory]
    // A sentence said twice, split by a period.
    [InlineData("First, fix it. And three. And three. Maybe ease it.", "First, fix it. And three, maybe ease it.")]
    [InlineData("We should leave. We should leave. It's late.", "We should leave. It's late.")]
    // A restart that changes a word keeps the second try.
    [InlineData("Because I've only defeated I've only finished the tornado four times.", "Because I've only finished the tornado four times.")]
    [InlineData("We have to leave we have to go now.", "We have to go now.")]
    // A refined second try keeps the refined one.
    [InlineData("Maybe an easier, a slightly easier boss.", "Maybe a slightly easier boss.")]
    // A list number joins its item; a period inside a description is closed up.
    [InlineData("The fixes would be one bigger window. Two. Have the rain take stamina.", "The fixes would be one bigger window. Two, have the rain take stamina.")]
    [InlineData("Maybe a slightly easier. Tornado thing.", "Maybe a slightly easier tornado thing.")]
    public void Repeats_and_restarts_are_cleaned(string spoken, string expected) => Assert.Equal(expected, Clean(spoken));

    [Theory]
    [InlineData("She likes cats, she likes dogs.")]
    [InlineData("It is what it is.")]
    [InlineData("The more you play, the more you learn.")]
    [InlineData("Two. That's how many I need.")]
    public void Ordinary_speech_is_left_alone(string spoken) => Assert.Equal(spoken, Clean(spoken));

    [Fact]
    public void Recheck_finds_and_clears_anything_still_flagged()
    {
        var o = new CleanupOptions();
        Assert.NotEmpty(TextCleaner.RemainingIssues("And three. And three.", o));
        Assert.NotEmpty(TextCleaner.RemainingIssues("I've only defeated I've only finished it.", o));
        Assert.Empty(TextCleaner.RemainingIssues("The report is done. It's in the shared folder.", o));
        // A repeat across a paragraph break goes too, and the break stays.
        Assert.Equal("First point.\n\nSecond point is here.", TextCleaner.Recheck("First point.\n\nFirst point. Second point is here.", o));
    }
}

public class FeedbackParagraphTests
{
    // The user's playtest feedback, as dictated: paragraphs must follow the points, and no repeats may survive.
    const string Raw =
        "I like the boss fight because it feels rewarding. Unlike Captain Cold, who could easily be defeated even solo or with a group of people, he wasn't very rewarding. What I like about this is how there's a deep struggle. You can fail him multiple times, even with a team, and you have to learn his moves. You have to learn how to evade them quickly on the spot. But I feel like if we were low rebirth players or didn't have any game passes, any of that, it would be a difficult boss. " +
        "Especially if you're in a random lobby with, of course, people who are in different age ranges so you can't chat with them. Or just people who just don't know how to play the game very well, you probably wouldn't ever be able to defeat the boss. I think you should definitely find some ways to fix this, such as increasing the window that you can attack him. " +
        "As well as maybe reducing damage, maybe based on your health stat, once it's above a certain point. And what I mean by reducing health damage is that maybe once it's above 10,000, maybe 20,000 or 30,000, then the damage just adapts your health. So then anything above that, the attacks still do the same amount of damage. So it gives like a consistent amount of difficulty between even high rebirth players or moderate rebirth level players. " +
        "But the boss fight felt extremely rewarding. And there should definitely be a big reward. If it's going to stay a difficult boss, then the reward should be massive. " +
        "Maybe even a full rebirth. And if there were a couple things that would have made the boss fight much easier to defeat, I would say those things would be one bigger attack window. Two. " +
        "Have the acid rain only take away your stamina. Because I've had a few instances where it takes away my health. And I will be low health for the rest of the boss. And three. And three. Maybe an easier, a slightly easier. Tornado thing. Because I've only defeated I've only finished the tornado thing maybe like four times at max. So maybe it's just a skill issue. But maybe ease that a bit. Make it easier.";

    [Fact]
    public void Paragraphs_start_only_where_the_point_shifts()
    {
        string text = TextCleaner.Process(Raw, new AppSettings { Style = WritingStyle.FormalName });
        var starts = text.Split("\n\n").Select(p => string.Join(" ", p.Split(' ').Take(3))).ToList();
        Assert.Equal(["I like the", "But I feel", "I think you", "But the boss", "And if there"], starts);
        Assert.DoesNotContain("And three. And three", text);
        Assert.DoesNotContain("I've only defeated", text);
        Assert.Contains("Two, have the acid rain", text);
        Assert.Contains("a slightly easier tornado thing", text);
        Assert.Empty(TextCleaner.RemainingIssues(text.Replace("\n\n", " "), new CleanupOptions()));
    }
}
