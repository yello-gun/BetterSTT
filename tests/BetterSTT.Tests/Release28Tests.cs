using BetterSTT;

namespace BetterSTT.Tests;

public class SpokenCodeTests
{
    [Theory]
    [InlineData("camel case user name", "userName")]
    [InlineData("Camel case user name.", "userName")]
    [InlineData("pascal case order service", "OrderService")]
    [InlineData("Rename the variable to camelCaseUserName.", "Rename the variable to userName")]
    [InlineData("call it snake_case_retry_count", "call it retry_count")]
    [InlineData("snake case max retry count", "max_retry_count")]
    [InlineData("kebab case main nav bar", "main-nav-bar")]
    [InlineData("constant case api key", "API_KEY")]
    [InlineData("all caps base url", "BASE_URL")]
    [InlineData("open the file config dot json", "open the file config.json")]
    [InlineData("camel case user name equals get user open paren close paren semicolon", "userName = get user();")]

    [InlineData("the path is src slash app slash main dot ts", "the path is src/app/main.ts")]
    [InlineData("if count double equals zero", "if count == zero")]
    public void Spoken_code_becomes_identifiers_and_symbols(string spoken, string expected) =>
        Assert.Equal(expected, SpokenCode.Convert(spoken));

    [Theory]
    [InlineData("Can you review this pull request?")]
    [InlineData("Let's refactor the login flow tomorrow.")]
    public void Plain_speech_is_left_alone(string text) =>
        Assert.Equal(text, SpokenCode.Convert(text));

    [Fact]
    public void Code_style_runs_after_cleanup()
    {
        var s = new AppSettings { Style = WritingStyle.CodeName };
        Assert.Equal("Rename it to userName", TextCleaner.Process("Um, rename it to, uh, camel case user name.", s));
    }
}

public class DictionaryTransferTests
{
    [Fact]
    public void Export_then_import_restores_everything()
    {
        var source = AppSettings.Sample();
        source.Vocabulary = "Claude, Kubernetes";
        string json = DictionaryTransfer.Export(source);

        var target = new AppSettings();
        var result = DictionaryTransfer.Import(json, target);
        Assert.Equal(source.Replacements.Count, result.Fixes);
        Assert.Equal(source.Snippets.Count, result.Snippets);
        Assert.Equal(2, result.Words);
        Assert.Equal("GitHub", target.Replacements.Single(r => r.From == "git hub").To);
        Assert.Contains("Best regards", target.Snippets.Single(s => s.Trigger == "my sign off").Text);
        Assert.Equal("Claude, Kubernetes", target.Vocabulary);
    }

    [Fact]
    public void Import_merges_instead_of_duplicating()
    {
        var target = new AppSettings { Vocabulary = "Claude" };
        target.Replacements.Add(new Replacement { From = "git hub", To = "Github" });
        DictionaryTransfer.Import("""{ "vocabulary": "claude, Sarah", "replacements": [ { "from": "Git Hub", "to": "GitHub" } ] }""", target);
        Assert.Single(target.Replacements);
        Assert.Equal("GitHub", target.Replacements[0].To);
        Assert.Equal("Claude, Sarah", target.Vocabulary);
    }

    [Theory]
    [InlineData("git hub,GitHub\ncloud code,Claude Code")]
    [InlineData("git hub => GitHub\ncloud code -> Claude Code")]
    [InlineData("heard,type\ngit hub,GitHub\ncloud code,Claude Code")]
    public void Lists_of_fixes_can_be_pasted_or_imported(string text)
    {
        var target = new AppSettings();
        var result = DictionaryTransfer.Import(text, target);
        Assert.Equal(2, result.Fixes);
        Assert.Equal("Claude Code", target.Replacements.Single(r => r.From == "cloud code").To);
    }
}

public class PrivateAndLanguageTests
{
    [Fact]
    public void Private_dictations_count_but_are_not_kept()
    {
        var h = new HistoryStore { Persist = false };
        var item = h.CountOnly("um, hello there", "Hello there.");
        Assert.Empty(h.Recent);
        Assert.Equal(1, h.Dictations);
        Assert.Equal(1, h.WordsRemoved);
        Assert.Equal("Hello there.", item.Clean);
    }

    [Theory]
    [InlineData("es", "Spanish")]
    [InlineData("en", "English")]
    [InlineData("ko", "Korean")]
    [InlineData(null, null)]
    public void Language_codes_have_names(string? code, string? name) =>
        Assert.Equal(name, AppSettings.LanguageName(code));

    [Fact]
    public void App_rule_can_turn_line_breaks_into_spaces()
    {
        var s = new AppSettings();
        Assert.True(s.ForApp("WindowsTerminal").JoinLines);
        Assert.False(s.ForApp("notepad").JoinLines);
    }
}
