using BetterSTT;

namespace BetterSTT.Tests;

public class ReasoningTests
{
    static LocalAi.ChatReply Reply(string content, string thinking = "", bool truncated = false) => new(content, thinking, truncated);

    [Theory]
    // A closing tag with no opening one: the opening tag was already in the prompt (Qwen3, DeepSeek-R1).
    [InlineData("Okay, the user wants an essay. Let me think about it.\n</think>\n\nFlashpoint is a Roblox game.", "Flashpoint is a Roblox game.")]
    // A whole block.
    [InlineData("<think>Let me draft this.</think>The report is done.", "The report is done.")]
    [InlineData("<THINK>\nreasoning\n</THINK>\n\nThe report is done.", "The report is done.")]
    // Reasoning that never finished: there is no answer.
    [InlineData("<think>\nOkay, the user wants a 250-word essay. Let me start by recalling", "")]
    [InlineData("The report is done.\n<think>and then I would", "The report is done.")]
    // Ordinary decoration around an answer.
    [InlineData("Here is the edited text:\nThe report is done.", "The report is done.")]
    [InlineData("```\nThe report is done.\n```", "The report is done.")]
    public void Reasoning_never_reaches_the_answer(string reply, string expected) =>
        Assert.Equal(expected, LocalAi.Finish(Reply(reply)).Text);

    [Fact]
    public void Reasoning_with_no_tags_is_recognised_by_how_it_reads()
    {
        // The start of what leaked into the user's essay.
        const string leaked = "Okay, the user wants a 250-word essay about Flashpoint on Roblox. Let me start by recalling what Flashpoint is. " +
                              "First, I need to make sure the essay hits exactly 250 words. I should focus on the game's mechanics.";
        Assert.True(LocalAi.LooksLikeReasoning(leaked));
        Assert.Equal("", LocalAi.Finish(Reply(leaked)).Text);
        Assert.True(LocalAi.Finish(Reply(leaked)).Reasoned);

        const string polishLeak = "We are given a text inside <text> tags. We must edit it following the rules.\n\n The original text: \"I have a steel sword\"";
        Assert.True(LocalAi.LooksLikeReasoning(polishLeak));

        // What qwen3:4b actually wrote when asked to polish the sword sentence.
        const string realLeak = "We are given a text to edit. The instructions say: Fix grammar and any sentence that doesn't make sense. Make it clear and direct.\n\n" +
                                "The original text: \"I have a steel sword as seen in the first image\"\n\nLet's check for grammar and clarity.\n\nThe sentence is a bit long.";
        Assert.True(LocalAi.LooksLikeReasoning(realLeak));
        Assert.Equal("", LocalAi.Finish(Reply(realLeak)).Text);
    }

    [Theory]
    [InlineData("Flashpoint is a Roblox survival game where players race a timer. It rewards quick decisions and teamwork.")]
    [InlineData("I like the boss fight because it feels rewarding.")]
    [InlineData("Okay, we can meet on Tuesday.")]
    public void Real_answers_are_not_mistaken_for_reasoning(string text)
    {
        Assert.False(LocalAi.LooksLikeReasoning(text));
        Assert.Equal(text, LocalAi.Finish(Reply(text)).Text);
    }

    [Fact]
    public void A_dictation_that_really_starts_like_reasoning_is_kept()
    {
        const string dictated = "Okay, the user wants to log in. Let me explain: the user needs to enter a password, and I should add a reset link.";
        Assert.True(LocalAi.LooksLikeReasoning(dictated));
        // The original reads the same way, so it isn't a leak.
        Assert.Equal(dictated, LocalAi.Finish(Reply(dictated), original: dictated).Text);
    }

    [Fact]
    public void Reasoning_reported_separately_is_noted_but_not_typed()
    {
        var done = LocalAi.Finish(Reply("The report is done.", thinking: "Let me think about how to edit this..."));
        Assert.Equal("The report is done.", done.Text);
        Assert.True(done.Reasoned);
    }

    [Fact]
    public void A_polish_is_given_little_room_so_a_thinking_model_is_cut_off_fast()
    {
        Assert.InRange(LocalAi.PolishBudget("I have a steel sword as seen in the first image, but in the second and third image it shows a rusty metal sword."), 160, 300);
        Assert.Equal(2048, LocalAi.PolishBudget(new string('x', 20_000)));
    }

    [Fact]
    public void Prompts_say_not_to_show_reasoning_or_swap_details()
    {
        string polish = LocalAi.PolishSystemPrompt("");
        Assert.Contains("Do not explain, plan or show your reasoning", polish);
        Assert.Contains("steel sword", polish);
        Assert.Contains("A contrast", polish);
        Assert.Contains("Do not explain, plan or show your reasoning", LocalAi.RewriteSystemPrompt("write an essay", hasText: false));
    }

    [Fact]
    public void A_model_that_keeps_thinking_is_remembered_until_ai_is_set_up_again()
    {
        LocalAi.MarkSlowThinker("test-model:1b");
        Assert.True(LocalAi.IsSlowThinker("test-model:1b"));
        LocalAi.ForgetLearned();
        Assert.False(LocalAi.IsSlowThinker("test-model:1b"));
    }
}

public class PolishGuardTests
{
    [Theory]
    // Details must not be swapped for others (the "steel sword" / "rusty metal sword" report).
    [InlineData("I have a steel sword as seen in the first image, but in the second and third image it shows a rusty metal sword.",
                "I have a rusty metal sword as seen in the first image, but the second and third images show a rusty metal sword.")]
    // Names and numbers must survive.
    [InlineData("Send the 12,000 invoices to Professor Adams by Thursday.", "Send the invoices to the professor by Thursday.")]
    [InlineData("The meeting with Sarah is at 4 pm on Thursday.", "The meeting with Sarah is on Thursday.")]
    public void Meaning_changes_are_rejected(string original, string edited) =>
        Assert.NotNull(LocalAi.CheckPolish(original, edited));

    // Edits the AI really made in earlier runs, with the rules' text they were made from.
    const string Report = "So I wanted to let you know that the report is done, and it is in the shared folder. Can you check it before Friday, because I am not sure the numbers in the second part make sense or whatever.";
    const string ReportPolished = "So I wanted to let you know that the report is done, and it is in the shared folder. Can you check it before Friday because I am not sure the numbers in the second part make sense.";
    const string Paper = "I wanted to ask about the research paper that's due on Thursday. I think my sources are solid, but the analysis section is not as strong as I want it to be. Would it be possible to submit it on Monday instead? I would still turn in the outline on time.";
    const string PaperReworded = "I wanted to ask about the research paper due on Thursday. I think my sources are solid, but I'm not satisfied with the analysis section. Would it be possible to submit it on Monday instead? I would still turn in the outline on time.";

    [Fact]
    public void Cutting_words_is_always_fine() => Assert.Null(LocalAi.CheckPolish(Report, ReportPolished));

    [Fact]
    public void Replacing_a_word_is_blocked_when_keeping_exact_words_and_allowed_otherwise()
    {
        const string original = "Please check the budget before Friday and send it to the finance team.";
        const string reworded = "Please review the budget before Friday and send it to the finance team.";
        Assert.Equal("the AI replaced one of your words with another", LocalAi.CheckPolish(original, reworded));
        Assert.Null(LocalAi.CheckPolish(original, reworded, keepWording: false));
    }

    [Fact]
    public void A_real_polish_that_cut_and_rearranged_words_is_accepted_in_both_modes()
    {
        Assert.Null(LocalAi.CheckPolish(Paper, PaperReworded));
        Assert.Null(LocalAi.CheckPolish(Paper, PaperReworded, keepWording: false));
    }

    [Fact]
    public void Copying_a_detail_over_another_is_blocked_either_way()
    {
        const string original = "I have a steel sword as seen in the first image, but in the second and third image it shows a rusty metal sword.";
        const string merged = "I have a rusty metal sword as seen in the first image, but the second and third images show a rusty metal sword.";
        Assert.Equal("the AI copied one detail over another", LocalAi.CheckPolish(original, merged));
        Assert.Equal("the AI copied one detail over another", LocalAi.CheckPolish(original, merged, keepWording: false));
    }

    [Theory]
    [InlineData("I have a steel sword as seen in the first image, but in the second and third image you can see that it shows a rusty metal sword.",
                "I have a steel sword as seen in the first image, but the second and third images show a rusty metal sword.")]
    [InlineData("So basically the the report is done and it is in the shared folder.", "The report is done and it is in the shared folder.")]
    [InlineData("Send the 12,000 invoices to Professor Adams by Thursday.", "Send the 12000 invoices to Professor Adams by Thursday.")]
    public void Light_edits_are_accepted(string original, string edited) =>
        Assert.Null(LocalAi.CheckPolish(original, edited));
}

public class EngineExplanationTests
{
    [Theory]
    [InlineData(unchecked((int)0xC0000005), "", "graphics driver")]
    [InlineData(unchecked((int)0xC0000135), "", "Visual C++ runtime")]
    [InlineData(unchecked((int)0xC000007B), "", "Visual C++ runtime")]
    [InlineData(unchecked((int)0xC000001D), "", "AVX")]
    [InlineData(3, "", "out of memory")]
    [InlineData(2, "PlatformNotSupportedException: AVX is not supported on this platform, and noavx runtime is not available.", "too old")]
    [InlineData(2, "DllNotFoundException: Unable to load DLL 'whisper' (VCRUNTIME140.dll)", "Visual C++ runtime")]
    [InlineData(2, "IOException: The file is in use.", "The file is in use")]
    public void Failures_are_explained_in_plain_words(int exitCode, string report, string expected) =>
        Assert.Contains(expected, EngineCheck.Describe(exitCode, report), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Missing_runtime_advice_points_to_the_download() =>
        Assert.Contains("vc_redist.x64.exe", EngineCheck.Advice("Windows is missing the Microsoft Visual C++ runtime"));

    [Fact]
    public void Results_are_remembered_per_runtime_and_forgotten_on_request()
    {
        var s = new AppSettings();
        EngineCheck.Store(s, new EngineResult { Key = "gpu|Card|model.bin|3.0.2", Ok = false, Detail = "crashed" });
        EngineCheck.Store(s, new EngineResult { Key = "cpu|-|model.bin|3.0.2", Ok = true });
        Assert.False(EngineCheck.Cached(s, "gpu|Card|model.bin|3.0.2")!.Ok);
        EngineCheck.Store(s, new EngineResult { Key = "gpu|Card|model.bin|3.0.2", Ok = true }); // replaces, doesn't duplicate
        Assert.Equal(2, s.EngineChecks.Count);
        EngineCheck.Forget(s, gpu: true);
        Assert.Null(EngineCheck.Cached(s, "gpu|Card|model.bin|3.0.2"));
        Assert.NotNull(EngineCheck.Cached(s, "cpu|-|model.bin|3.0.2"));
    }

    [Fact]
    public void A_new_card_model_or_version_is_checked_again()
    {
        var s = new AppSettings();
        string key = EngineCheck.Key(true, s);
        s.ModelType = Whisper.net.Ggml.GgmlType.SmallEn;
        Assert.NotEqual(key, EngineCheck.Key(true, s));
        Assert.Equal(EngineCheck.Key(false, s), EngineCheck.Key(false, s));
    }
}
