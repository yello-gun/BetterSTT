using BetterSTT;

namespace BetterSTT.Tests;

public class PointShiftTests
{
    static readonly WritingStyle Formal = WritingStyle.Formal();
    static string P(string text) => Paragraphs.Format(text, Formal);

    [Fact]
    public void A_contrast_starts_a_paragraph() => Assert.Equal(
        "The new design is faster and the team likes it. It also uses less memory.\n\nOn the other hand, it breaks the old plugins. That would upset our biggest customers.",
        P("The new design is faster and the team likes it. It also uses less memory. On the other hand, it breaks the old plugins. That would upset our biggest customers."));

    [Fact]
    public void A_shift_in_time_starts_a_paragraph() => Assert.Equal(
        "We finished the migration today and everything is running. The dashboards look normal.\n\nNext week the billing team moves over. They'll need access to the new database.",
        P("We finished the migration today and everything is running. The dashboards look normal. Next week the billing team moves over. They'll need access to the new database."));

    [Fact]
    public void Turning_to_a_request_starts_a_paragraph() => Assert.Equal(
        "I checked the research paper this morning. My sources are solid, but the analysis is weak.\n\nWould it be possible to submit it on Monday instead? I would still turn in the outline on time.",
        P("I checked the research paper this morning. My sources are solid, but the analysis is weak. Would it be possible to submit it on Monday instead? I would still turn in the outline on time."));

    [Fact]
    public void A_change_of_subject_without_a_marker_starts_a_paragraph() => Assert.Equal(
        "The kitchen renovation is almost done. The cabinets arrived and the counters are installed.\n\nMy car failed its inspection yesterday. The mechanic says the brakes need replacing.",
        P("The kitchen renovation is almost done. The cabinets arrived and the counters are installed. My car failed its inspection yesterday. The mechanic says the brakes need replacing."));

    [Fact]
    public void Words_that_point_back_keep_sentences_together() => Assert.Equal(
        "We moved the launch to Friday. This gives design another week. It also means marketing can finish the video.",
        P("We moved the launch to Friday. This gives design another week. It also means marketing can finish the video."));

    [Fact]
    public void A_new_topic_in_a_run_of_long_sentences_starts_a_paragraph()
    {
        string text =
            "The server migration took most of the week because the database was larger than we expected and the copy kept timing out. " +
            "We split the database copy into smaller batches and the migration finished on Thursday night without losing any records. " +
            "The database is now on the new server and response times dropped by about half compared with the old machine. " +
            "Our designers spent the same week redrawing every icon in the mobile app so they match the new brand colors. " +
            "The new icons ship with the next app update, and the designers still need feedback on the settings screen icons.";
        string result = P(text);
        Assert.Contains("\n\nOur designers", result);
        Assert.Equal(1, result.Split("\n\n").Length - 1);
    }

    [Fact]
    public void A_long_paragraph_on_one_topic_gets_one_pause_where_it_is_least_connected()
    {
        string[] s =
        [
            "The budget review covered every department's spending for the full year and compared it with the budget we approved in January.",
            "Most departments stayed within their budget, although marketing spent more on events than the budget allowed.",
            "The marketing overspend came from two trade shows that we added late in the year after the budget was set.",
            "For next year the finance team wants every department to submit a quarterly spending forecast alongside the budget.",
            "Those quarterly forecasts would let finance catch overspending early instead of finding it at the annual review.",
            "Finance will send a forecast template to each department head before the end of the month.",
        ];
        string result = P(string.Join(" ", s));
        var parts = result.Split("\n\n");
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("For next year", parts[1]); // between what happened and what changes next
    }

    [Fact]
    public void Fewer_and_more_change_how_readily_it_breaks()
    {
        const string text = "The kitchen renovation is almost done. The cabinets arrived and the counters are installed. My car failed its inspection yesterday. The mechanic says the brakes need replacing.";
        var fewer = WritingStyle.Formal(); fewer.ParagraphEagerness = ParagraphEagerness.Fewer;
        Assert.DoesNotContain("\n\n", Paragraphs.Format(text, fewer));
    }
}

public class WordinessTests
{
    static string T(string s) => Wordiness.Tighten(s, Wordiness.DefaultEmptyWords());

    [Theory]
    [InlineData("I just wanted to let you know that the report is done.", "The report is done.")]
    [InlineData("We moved the date due to the fact that the venue was booked.", "We moved the date because the venue was booked.")]
    [InlineData("In order to finish on time, we need a large number of volunteers.", "To finish on time, we need many volunteers.")]
    [InlineData("At this point in time we are in the process of reviewing each and every application.", "Now we are reviewing every application.")]
    [InlineData("We need to make a decision about the venue in the near future.", "We need to decide about the venue soon.")]
    [InlineData("The end result was a really very good outcome.", "The result was a good outcome.")]
    [InlineData("It is important to note that the office is closed on Friday.", "The office is closed on Friday.")]
    [InlineData("Basically, I'm actually quite happy with it.", "Basically, I'm happy with it.")]
    [InlineData("With regard to the budget, it still remains the same.", "About the budget, it remains the same.")]
    public void Wordiness_is_cut(string input, string expected) => Assert.Equal(expected, T(input));

    [Theory]
    [InlineData("I'm not really sure about that.")]
    [InlineData("Thank you very much for your help.")]
    [InlineData("We reached the very end of the list.")]
    [InlineData("I want to tell you about the trip.")]
    [InlineData("The order is due to arrive tomorrow.")]
    public void Meaning_is_kept(string input) => Assert.Equal(input, T(input));

    [Fact]
    public void Formal_is_concise_by_default_and_natural_is_not()
    {
        Assert.True(WritingStyle.Formal().IsConcise);
        Assert.False(WritingStyle.Natural().IsConcise);
        Assert.Equal("The report is done.",
            TextCleaner.Process("Um, I just wanted to let you know that the report is done.", new CleanupOptions(), [], WritingStyle.Formal()));
    }
}