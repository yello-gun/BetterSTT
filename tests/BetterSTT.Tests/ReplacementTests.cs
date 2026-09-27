using BetterSTT;

namespace BetterSTT.Tests;

public class ReplacementTests
{
    static Replacement R(string from, string to) => new() { From = from, To = to };

    [Theory]
    [InlineData("I pushed it to git hub today.", "I pushed it to GitHub today.")]
    [InlineData("Git-hub is down.", "GitHub is down.")]
    [InlineData("Use GIT   HUB for this.", "Use GitHub for this.")]
    [InlineData("The github page.", "The GitHub page.")]
    public void Fixes_words_and_phrases(string input, string expected)
    {
        var fixes = new[] { R("git hub", "GitHub"), R("github", "GitHub") };
        Assert.Equal(expected, TextCleaner.ApplyReplacements(input, fixes));
    }

    [Fact]
    public void Only_whole_words_are_replaced()
    {
        var fixes = new[] { R("cloud", "Claude") };
        Assert.Equal("Ask Claude about cloudy weather.", TextCleaner.ApplyReplacements("Ask cloud about cloudy weather.", fixes));
    }

    [Fact]
    public void Longest_phrase_wins()
    {
        var fixes = new[] { R("visual studio", "Visual Studio"), R("visual studio code", "VS Code") };
        Assert.Equal("Open VS Code now.", TextCleaner.ApplyReplacements("Open visual studio code now.", fixes));
    }

    [Fact]
    public void Fixes_do_not_chain()
    {
        var fixes = new[] { R("alpha", "beta"), R("beta", "gamma") };
        Assert.Equal("beta gamma", TextCleaner.ApplyReplacements("alpha beta", fixes));
    }

    [Fact]
    public void Hyphenated_fix_does_not_crash()
    {
        var fixes = new[] { R("e-mail", "email") };
        Assert.Equal("Send an email.", TextCleaner.ApplyReplacements("Send an e-mail.", fixes));
        Assert.Equal("Send an email.", TextCleaner.ApplyReplacements("Send an e mail.", fixes));
    }

    [Fact]
    public void Process_runs_cleanup_then_fixes()
    {
        var fixes = new[] { R("git hub", "GitHub") };
        Assert.Equal("Push it to GitHub.", TextCleaner.Process("Um, push it to, uh, git hub.", new CleanupOptions(), fixes));
    }

    [Fact]
    public void Exact_words_skips_cleanup_and_fixes()
    {
        var fixes = new[] { R("git hub", "GitHub") };
        var exact = new CleanupOptions { Enabled = false };
        Assert.Equal("Um, push it to git hub.", TextCleaner.Process("Um, push it to git hub.", exact, fixes));
    }

    [Fact]
    public void Fixed_words_become_recognition_hints()
    {
        var s = new AppSettings { Vocabulary = "Kubernetes", Replacements = [R("git hub", "GitHub")] };
        Assert.EndsWith("Names and terms: Kubernetes, GitHub.", s.BuildPrompt());
    }
}

public class AppProfileTests
{
    [Fact]
    public void Profile_overrides_only_what_it_sets()
    {
        var s = new AppSettings
        {
            AddTrailingSpace = true,
            OutputMethod = OutputMethod.Paste,
            AppProfiles = [new() { ProcessName = "WindowsTerminal", AddTrailingSpace = false }],
        };

        var terminal = s.ForApp("windowsterminal");
        Assert.False(terminal.AddTrailingSpace);
        Assert.Equal(OutputMethod.Paste, terminal.OutputMethod);
        Assert.True(terminal.Cleanup.Enabled);

        Assert.True(s.ForApp("chrome").AddTrailingSpace);
        Assert.True(s.ForApp(null).AddTrailingSpace);
    }

    [Fact]
    public void Exact_words_profile_turns_cleanup_off()
    {
        var s = new AppSettings { AppProfiles = [new() { ProcessName = "code", ExactWords = true }] };
        Assert.False(s.ForApp("Code").Cleanup.Enabled);
        Assert.True(s.Cleanup.Enabled); // the original is untouched
    }

    [Fact]
    public void Windows_Terminal_has_no_trailing_space_by_default()
    {
        Assert.False(new AppSettings().ForApp("WindowsTerminal").AddTrailingSpace);
    }
}
