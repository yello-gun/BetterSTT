using System.Text.RegularExpressions;

namespace BetterSTT;

/// <summary>
/// Removes words that add nothing to what a message means, following plain-language guides (plainlanguage.gov,
/// university writing centres): stock wordy phrases ("due to the fact that" → "because"), redundant pairs
/// ("each and every" → "every"), buried verbs ("make a decision" → "decide"), throat-clearing openers
/// ("I just wanted to let you know that …") and empty intensifiers ("really", "basically"). Only swaps that keep the
/// meaning are made; anything that could change it (a "not really", "thank you very much") is left alone.
/// </summary>
public static class Wordiness
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Wordy phrase → the plain word. Longest first when applied.</summary>
    static readonly (string From, string To)[] Phrases =
    [
        // Stock phrases
        ("due to the fact that", "because"), ("owing to the fact that", "because"), ("in view of the fact that", "because"),
        ("in light of the fact that", "because"), ("on account of the fact that", "because"), ("given the fact that", "because"),

        ("in spite of the fact that", "although"), ("despite the fact that", "although"), ("regardless of the fact that", "although"),
        ("in order to", "to"), ("so as to", "to"), ("in an effort to", "to"), ("in order for", "for"), ("in order that", "so that"),
        ("at this point in time", "now"), ("at this moment in time", "now"), ("at the present time", "now"),
        ("at that point in time", "then"), ("in the near future", "soon"), ("in the event that", "if"), ("until such time as", "until"),
        ("with regard to", "about"), ("in regard to", "about"), ("with regards to", "about"), ("with reference to", "about"),
        ("with respect to", "about"), ("in relation to", "about"), ("pertaining to", "about"), ("in connection with", "about"),
        ("a large number of", "many"), ("a great number of", "many"), ("an appreciable number of", "many"),
        ("a majority of", "most"), ("the majority of", "most"), ("a small number of", "a few"),
        ("in close proximity to", "near"), ("with the exception of", "except"), ("in lieu of", "instead of"),
        ("during the course of", "during"), ("during the period of", "during"), ("for a period of", "for"),
        ("on a daily basis", "daily"), ("on a weekly basis", "weekly"), ("on a monthly basis", "monthly"),
        ("on a regular basis", "regularly"), ("in a timely manner", "promptly"), ("as a means of", "to"),
        ("in the process of ", ""), ("is able to", "can"), ("are able to", "can"), ("has the ability to", "can"),
        ("have the ability to", "can"), ("is in need of", "needs"), ("are in need of", "need"), ("has a need for", "needs"),
        ("it is requested that you", "please"), ("as a matter of fact", "in fact"),
        // Buried verbs (nominalizations)
        ("make a decision to", "decide to"), ("make a decision", "decide"), ("made a decision to", "decided to"),
        ("made a decision", "decided"), ("come to a conclusion", "conclude"), ("came to a conclusion", "concluded"),
        ("give consideration to", "consider"), ("take into consideration", "consider"), ("provide assistance to", "help"),
        ("conduct an investigation of", "investigate"), ("conduct an investigation into", "investigate"),
        ("make an assumption", "assume"), ("have a discussion about", "discuss"), ("had a discussion about", "discussed"),
        ("give an explanation of", "explain"), ("reach an agreement", "agree"), ("reached an agreement", "agreed"),
        // Redundant pairs and modifiers
        ("each and every", "every"), ("first and foremost", "first"), ("any and all", "all"), ("one and only", "only"),
        ("end result", "result"), ("final outcome", "outcome"), ("past history", "history"), ("advance planning", "planning"),
        ("future plans", "plans"), ("free gift", "gift"), ("basic fundamentals", "fundamentals"), ("true fact", "fact"),
        ("past experience", "experience"), ("unexpected surprise", "surprise"), ("new innovation", "innovation"),
        ("completely finished", "finished"), ("absolutely essential", "essential"), ("still remains", "remains"),
        ("still remain", "remain"), ("repeat again", "repeat"), ("revert back", "revert"), ("return back", "return"),
        ("combine together", "combine"), ("join together", "join"), ("collaborate together", "collaborate"),
        ("each individual", "each"), ("plan ahead", "plan"), ("few in number", "few"), ("close proximity", "proximity"),
        ("exact same", "same"), ("added bonus", "bonus"), ("brief summary", "summary"), ("general consensus", "consensus"),
    ];

    /// <summary>Openers that only announce the message ("I just wanted to let you know that …").</summary>
    static readonly Regex ThroatClearing = new(
        // "let you know" may drop its "that"; "say", "mention" and "tell you" only count with it ("I want to tell
        // you about the trip" says something).
        @"(?<=^|[.!?]\s+|^(?:hi|hello|hey|dear|good\s+(?:morning|afternoon|evening))\b[^,.!?]{0,40},\s+)(?:so,?\s+)?(?:(?:basically,?\s+)?the\s+reason(?:\s+why)?\s+(?:is|was)(?:\s+that|\s+because)?|i\s+(?:just\s+)?(?:wanted|want|would\s+like|'d\s+like)\s+to\s+(?:let\s+you\s+know(?:\s+that)?|(?:say|mention|point\s+out|tell\s+you)\s+that)|" +
        @"i'?m\s+(?:just\s+)?(?:writing|reaching\s+out)\s+to\s+let\s+you\s+know(?:\s+that)?|" +
        @"(?:it\s+(?:is|'s)\s+(?:important|worth)\s+(?:to\s+note|noting)|it\s+should\s+be\s+noted|it\s+goes\s+without\s+saying|let\s+me\s+just\s+say)(?:\s+that)?|" +
        @"needless\s+to\s+say|what\s+i'?m\s+trying\s+to\s+say\s+is|what\s+i\s+mean\s+is|the\s+thing\s+is)" +
        @",?\s+(?=\p{L})",
        Opts);

    public static List<string> DefaultEmptyWords() =>
        ["really", "very", "basically", "actually", "literally", "essentially", "totally", "quite", "simply"];

    /// <summary>Removes wordiness. <paramref name="emptyWords"/> are intensifiers dropped when they add nothing.</summary>
    public static string Tighten(string text, IReadOnlyList<string> emptyWords)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string s = ThroatClearing.Replace(text, "\u0001");

        foreach (var (from, to) in Phrases.OrderByDescending(p => p.From.Length))
        {
            string pattern = string.Join(@"\s+", from.Trim().Split(' ').Select(Regex.Escape));
            s = Regex.Replace(s, $@"(?<![\p{{L}}'])({pattern})(?![\p{{L}}'])" + (from.EndsWith(' ') ? @"\s*" : ""), m => MatchCase(m.Value, to), Opts);
        }

        var words = emptyWords.Select(w => w.Trim()).Where(w => w.Length > 0).Select(Regex.Escape).ToList();
        if (words.Count > 0)
        {
            // Kept where they change the meaning or are part of a fixed phrase: "not really", "very much",
            // "the very end", "really?", "actually, no".
            var rx = new Regex(
                $@"(?<!\b(?:not|n't|the|this|that|your|my)\s)(?<!n't\s)\b(?:{string.Join("|", words)})\b(?!\s+(?:much|well|many|few|first|last|same|best|least|own|end|beginning)\b)(?=\s+[\p{{L}}])(?!\s+(?:not|no)\b)\s+",
                Opts);
            s = rx.Replace(s, m => char.IsUpper(m.Value[0]) ? "\u0001" : "");
        }
        return Tidy(s);
    }

    static string MatchCase(string original, string replacement) =>
        replacement.Length > 0 && char.IsUpper(original[0]) ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement;

    static string Tidy(string s)
    {
        // \u0001 marks where a sentence's first words were removed: capitalize what now starts it.
        s = Regex.Replace(s, "\u0001+\\s*(\\p{L})", m => char.ToUpperInvariant(m.Groups[1].Value[0]).ToString());
        s = s.Replace("\u0001", "");
        s = Regex.Replace(s, @" {2,}", " ");
        s = Regex.Replace(s, @" +([,.!?;:])", "$1");
        s = Regex.Replace(s, @"^\s*,\s*", "");
        return s.Trim();
    }
}
