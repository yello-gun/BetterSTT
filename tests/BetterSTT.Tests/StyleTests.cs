using BetterSTT;

namespace BetterSTT.Tests;

public class StutterTests
{
    static readonly CleanupOptions Strict = new();

    [Theory]
    [InlineData("I think I think we should go.", "I think we should go.")]
    [InlineData("We should, we should leave now.", "We should leave now.")]
    [InlineData("We were go- going to the store.", "We were going to the store.")]
    [InlineData("It's a big—bigger problem.", "It's a bigger problem.")]
    [InlineData("Can you re-read the email?", "Can you re-read the email?")]
    [InlineData("It's a well-known fact.", "It's a well-known fact.")]
    [InlineData("I had had enough.", "I had had enough.")]
    public void Fixes_stutters_and_restarts(string input, string expected) =>
        Assert.Equal(expected, TextCleaner.Clean(input, Strict));
}

public class VagueEndingTests
{
    static readonly List<string> Endings = WritingStyle.DefaultVagueEndings();

    [Theory]
    [InlineData("We could grab lunch or something.", "We could grab lunch.")]
    [InlineData("We could grab lunch or something, then head back.", "We could grab lunch, then head back.")]
    [InlineData("Bring snacks and stuff like that.", "Bring snacks.")]
    [InlineData("It's quicker this way, if that makes sense.", "It's quicker this way.")]
    [InlineData("Is there something I can help with?", "Is there something I can help with?")]
    [InlineData("Or something else entirely.", "Or something else entirely.")]
    public void Removes_only_clause_ending_vagueness(string input, string expected) =>
        Assert.Equal(expected, TextCleaner.RemoveVagueEndings(input, Endings));
}

public class ParagraphTests
{
    static readonly WritingStyle Formal = WritingStyle.Formal();

    [Fact]
    public void Greeting_topic_change_and_sign_off_get_their_own_paragraphs()
    {
        const string text = "Hi John, I looked at the report this morning. The numbers look right. " +
                            "Also, the deadline moved to Friday. We should plan for that. Thanks.";
        Assert.Equal(
            "Hi John,\n\nI looked at the report this morning. The numbers look right.\n\n" +
            "Also, the deadline moved to Friday. We should plan for that.\n\nThanks.",
            TextCleaner.FormatParagraphs(text, Formal));
    }

    [Fact]
    public void Long_runs_are_split_after_the_maximum_sentences()
    {
        var style = WritingStyle.Formal();
        style.MaxSentencesPerParagraph = 2;
        Assert.Equal("One. Two.\n\nThree. Four.\n\nFive.", TextCleaner.FormatParagraphs("One. Two. Three. Four. Five.", style));
    }

    [Fact]
    public void A_single_sentence_is_left_alone()
    {
        Assert.Equal("Sounds good to me.", TextCleaner.FormatParagraphs("Sounds good to me.", Formal));
    }

    [Fact]
    public void Starter_words_must_be_whole_words()
    {
        // "Alsoa" is not "Also", "Nextdoor" is not "Next".
        Assert.Equal("The house is nice. Nextdoor is loud.",
            TextCleaner.FormatParagraphs("The house is nice. Nextdoor is loud.", Formal));
    }

    [Fact]
    public void Formal_style_runs_the_whole_pipeline()
    {
        const string raw = "Um, hi Sarah, so I was thinking we could, uh, meet Tuesday or something. " +
                           "Also, can you send the the slides and stuff?";
        Assert.Equal(
            "Hi Sarah,\n\nSo I was thinking we could meet Tuesday.\n\nAlso, can you send the slides?",
            TextCleaner.Process(raw, new CleanupOptions(), [], Formal));
    }

    [Fact]
    public void Style_page_sample_comes_out_as_a_formal_message()
    {
        const string spoken = "Um, hi Sarah, so I was thinking... like, maybe we could, uh, meet on Tuesday or something. " +
                              "I think I think the the new approach is better. Also, can you send the slides and stuff? Thanks.";
        Assert.Equal(
            "Hi Sarah,\n\nSo I was thinking maybe we could meet on Tuesday. I think the new approach is better.\n\n" +
            "Also, can you send the slides?\n\nThanks.",
            TextCleaner.Process(spoken, new CleanupOptions(), [], Formal));
    }

    [Fact]
    public void Natural_style_keeps_vague_endings_and_single_paragraph()
    {
        Assert.Equal("Meet Tuesday or something. Also, bring slides.",
            TextCleaner.Process("Meet Tuesday or something. Also, bring slides.", new CleanupOptions(), [], WritingStyle.Natural()));
    }

    [Fact]
    public void Exact_words_style_returns_what_was_heard()
    {
        Assert.Equal("Um, meet Tuesday or something.",
            TextCleaner.Process("Um,  meet Tuesday or something.", new CleanupOptions(), [], WritingStyle.Exact()));
    }
}

public class StyleSettingsTests
{
    [Fact]
    public void Old_exact_words_switch_becomes_the_exact_words_style()
    {
        var s = new AppSettings
        {
            Cleanup = new CleanupOptions { Enabled = false },
            AppProfiles = [new() { ProcessName = "Code", ExactWords = true }],
            Styles = [],
        };
        s.Migrate();

        Assert.Equal(WritingStyle.ExactName, s.Style);
        Assert.True(s.Cleanup.Enabled);
        Assert.Equal(WritingStyle.ExactName, s.AppProfiles[0].Style);
        Assert.Equal([WritingStyle.ExactName, WritingStyle.NaturalName, WritingStyle.FormalName, WritingStyle.MathName], s.Styles.Select(x => x.Name));
    }

    [Fact]
    public void App_style_overrides_the_usual_style()
    {
        var s = new AppSettings { AppProfiles = [new() { ProcessName = "OUTLOOK", Style = WritingStyle.FormalName }] };
        Assert.Equal(WritingStyle.FormalName, s.ForApp("outlook").CurrentStyle.Name);
        Assert.Equal(WritingStyle.NaturalName, s.ForApp("notepad").CurrentStyle.Name);
    }

    [Fact]
    public void Unknown_style_falls_back_to_natural()
    {
        Assert.Equal(WritingStyle.NaturalName, new AppSettings { Style = "Deleted style" }.CurrentStyle.Name);
    }
}
