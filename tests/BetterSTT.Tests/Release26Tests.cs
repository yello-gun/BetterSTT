using System.Text.Json;
using BetterSTT;

namespace BetterSTT.Tests;

public class SpokenMathTests
{
    [Theory]
    [InlineData("X squared plus one over two.", "x² + 1/2")]
    [InlineData("Two plus two equals four.", "2 + 2 = 4")]
    [InlineData("The square root of sixteen equals four.", "√16 = 4")]
    [InlineData("Minus five times three", "-5 × 3")]
    [InlineData("x is less than or equal to twenty five", "x ≤ 25")]
    [InlineData("pi r squared", "π r²")]
    [InlineData("open parenthesis x plus one close parenthesis squared", "(x + 1)²")]
    [InlineData("three point one four", "3.14")]
    [InlineData("y equals two x plus three", "y = 2x + 3")]
    [InlineData("a to the power of n", "aⁿ")]
    [InlineData("e to the third power", "e³")]
    [InlineData("x sub one plus x sub two", "x₁ + x₂")]
    [InlineData("x equals minus four", "x = -4")]
    [InlineData("ten divided by two is not equal to three", "10 ÷ 2 ≠ 3")]
    [InlineData("theta equals ninety degrees", "θ = 90°")]
    [InlineData("one hundred twenty three plus seven", "123 + 7")]
    public void Symbols(string spoken, string expected) =>
        Assert.Equal(expected, SpokenMath.Convert(spoken, MathFormat.Symbols));

    [Theory]
    [InlineData("x squared plus one over two", @"x^2 + \frac{1}{2}")]
    [InlineData("square root of x plus one", @"\sqrt{x} + 1")]
    [InlineData("a to the power of ten", @"a^{10}")]
    [InlineData("alpha times beta", @"\alpha \times \beta")]
    [InlineData("x is greater than or equal to zero", @"x \geq 0")]
    public void Latex(string spoken, string expected) =>
        Assert.Equal(expected, SpokenMath.Convert(spoken, MathFormat.Latex));

    [Theory]
    [InlineData("I have two cats and one dog.")]
    [InlineData("Let's meet at the one on the left.")]
    public void Plain_speech_is_left_alone(string text) =>
        Assert.Equal(text, SpokenMath.Convert(text, MathFormat.Symbols));

    [Fact]
    public void Off_changes_nothing() =>
        Assert.Equal("x squared", SpokenMath.Convert("x squared", MathFormat.Off));

    [Fact]
    public void Math_style_runs_after_cleanup()
    {
        var s = new AppSettings { Style = WritingStyle.MathName };
        Assert.Equal("x² + 1 = 5", TextCleaner.Process("Um, x squared plus, uh, one equals five.", s));
    }
}

public class SnippetTests
{
    static readonly Snippet[] Snippets =
    [
        new() { Trigger = "my email", Text = "sam@example.com" },
        new() { Trigger = "my sign off", Text = "Best regards,\r\nSam" },
    ];

    [Theory]
    [InlineData("My email.", "sam@example.com")]
    [InlineData("my EMAIL", "sam@example.com")]
    [InlineData("Um, my email.", "sam@example.com")]
    [InlineData("My sign-off!", "Best regards,\nSam")]
    public void Whole_dictation_matching_a_trigger_becomes_the_snippet(string spoken, string expected) =>
        Assert.Equal(expected, TextCleaner.Process(spoken, new CleanupOptions(), [], WritingStyle.Natural(), Snippets));

    [Fact]
    public void Trigger_inside_a_sentence_is_not_expanded() =>
        Assert.Equal("Send it to my email please.",
            TextCleaner.Process("Send it to my email please.", new CleanupOptions(), [], WritingStyle.Natural(), Snippets));

    [Fact]
    public void Snippets_work_in_exact_words_too() =>
        Assert.Equal("sam@example.com",
            TextCleaner.Process("My email.", new CleanupOptions(), [], WritingStyle.Exact(), Snippets));

    [Fact]
    public void Empty_snippet_text_is_ignored() =>
        Assert.Null(TextCleaner.MatchSnippet("my email", [new Snippet { Trigger = "my email", Text = "  " }]));
}

public class UpdaterTests
{
    [Theory]
    [InlineData("v2.6.1", 2, 6, 1)]
    [InlineData("2.6", 2, 6, 0)]
    [InlineData("V10.0.3", 10, 0, 3)]
    public void Parses_versions(string text, int major, int minor, int build)
    {
        Assert.True(Updater.TryParseVersion(text, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v2.6.0-beta")]
    [InlineData("")]
    public void Rejects_other_tags(string text) => Assert.False(Updater.TryParseVersion(text, out _));

    const string ReleaseJson = """
        {
          "tag_name": "v2.7.0",
          "assets": [
            { "name": "notes.txt", "size": 5, "browser_download_url": "https://github.com/yello-gun/BetterSTT/releases/download/v2.7.0/notes.txt" },
            { "name": "BetterSTT-Setup-2.7.0.exe", "size": 1234, "digest": "sha256:ABCDEF",
              "browser_download_url": "https://github.com/yello-gun/BetterSTT/releases/download/v2.7.0/BetterSTT-Setup-2.7.0.exe" }
          ]
        }
        """;

    [Fact]
    public void Finds_the_installer_in_a_release()
    {
        using var doc = JsonDocument.Parse(ReleaseJson);
        var release = Updater.ParseRelease(doc.RootElement);
        Assert.NotNull(release);
        Assert.Equal(new Version(2, 7, 0), release!.Version);
        Assert.EndsWith("BetterSTT-Setup-2.7.0.exe", release.Url);
        Assert.Equal(1234, release.Size);
        Assert.Equal("ABCDEF", release.Sha256);
    }

    [Fact]
    public void Ignores_installers_hosted_anywhere_else()
    {
        using var doc = JsonDocument.Parse(ReleaseJson.Replace("github.com/yello-gun/BetterSTT", "example.com/evil"));
        Assert.Null(Updater.ParseRelease(doc.RootElement));
    }
}

public class Settings26Tests
{
    [Fact]
    public void App_language_overrides_the_usual_one()
    {
        var s = new AppSettings { Language = "en" };
        s.AppProfiles.Add(new AppProfile { ProcessName = "WhatsApp", Language = "es" });
        Assert.Equal("es", s.ForApp("whatsapp").Language);
        Assert.Equal("en", s.ForApp("notepad").Language);
    }

    [Fact]
    public void Older_settings_gain_the_math_style()
    {
        var s = new AppSettings { Styles = [WritingStyle.Exact(), WritingStyle.Natural(), WritingStyle.Formal()] };
        s.Migrate();
        Assert.Contains(s.Styles, st => st.Name == WritingStyle.MathName && st.SpokenMath == MathFormat.Symbols);
    }

    [Fact]
    public void Diagnostics_hide_personal_text()
    {
        var s = AppSettings.Sample();
        s.Vocabulary = "Sarah Nguyen";
        string json = Diagnostics.RedactedSettings(s).ToJson();
        Assert.DoesNotContain("Sarah", json);
        Assert.DoesNotContain("sam@example.com", json);
        Assert.DoesNotContain("git hub", json);
        Assert.Contains(Diagnostics.Hidden, json);
        Assert.Equal("sam@example.com", s.Snippets[0].Text); // the real settings are untouched
    }
}
