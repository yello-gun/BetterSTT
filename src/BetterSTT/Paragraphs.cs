using System.Text.RegularExpressions;

namespace BetterSTT;

/// <summary>How readily a style starts new paragraphs.</summary>
public enum ParagraphEagerness { Fewer, Balanced, More }

/// <summary>
/// Splits text into paragraphs where the point shifts, the way writers do, not after a set number of sentences.
/// Writing guides (Purdue OWL and others) give these reasons for a new paragraph, and each becomes a signal:
/// <list type="bullet">
/// <item>A new idea or point. Explicit markers ("Also", "Another thing", "Next") announce one; without a marker, the
///   vocabulary changes, since a point is discussed with its own set of words (Hearst's TextTiling rests on this).</item>
/// <item>Contrast, to set the other side apart ("However", "On the other hand").</item>
/// <item>A shift in time or place, so the reader re-orients ("Yesterday", "Next week", "Back at the office").</item>
/// <item>A change of focus, which in messages is usually turning from informing to asking ("Can you…", "Please…").</item>
/// <item>Introduction and conclusion: the greeting, a wrap-up ("Overall", "In summary") and the sign-off.</item>
/// <item>The reader needs a pause: a long, dense paragraph is split where its sentences are least connected.</item>
/// </list>
/// Words that point back ("it", "this", "they", "so", "because") tie a sentence to the one before, so they keep it in
/// the same paragraph, and one-sentence paragraphs are avoided unless the signal is strong.
/// </summary>
public static class Paragraphs
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly Regex SentenceBreak = new(@"(?<=[.!?][""”’)]?)\s+(?=[\p{Lu}\p{N}""“(])");
    static readonly Regex Greeting = new(
        @"^(?<g>(?:hi|hello|hey|dear|greetings|good\s+(?:morning|afternoon|evening))\b[^,.!?]{0,40}[,!])\s*(?<rest>.*)$", Opts);
    static readonly Regex SignOff = new(
        @"^(?:thanks|thank\s+you|many\s+thanks|best|best\s+regards|kind\s+regards|regards|cheers|sincerely|talk\s+(?:soon|to\s+you\s+soon)|see\s+you(?:\s+soon)?|take\s+care)\b", Opts);

    // Contrast: the other side of the argument.
    static readonly Regex Contrast = new(
        @"^(?:however|on\s+the\s+other\s+hand|that\s+said|having\s+said\s+that|in\s+contrast|on\s+the\s+contrary|alternatively|then\s+again|even\s+so|nevertheless|nonetheless)\b", Opts);
    // Wrap-up: the conclusion.
    static readonly Regex WrapUp = new(
        @"^(?:overall|in\s+summary|to\s+sum\s+up|in\s+short|in\s+conclusion|all\s+in\s+all|bottom\s+line|long\s+story\s+short|anyway,?\s+that'?s)\b", Opts);
    // A shift in time or place, at the start of a sentence.
    static readonly Regex TimeOrPlace = new(
        @"^(?:yesterday|today|tonight|tomorrow|this\s+(?:morning|afternoon|evening|week|weekend|month|year)|" +
        @"(?:last|next)\s+(?:night|week|weekend|month|year|time|monday|tuesday|wednesday|thursday|friday|saturday|sunday)|" +
        @"(?:on|by|this|every)\s+(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday|the\s+weekend)|" +
        @"later(?:\s+(?:on|today|that\s+day))?|afterwards?|after\s+that|earlier(?:\s+today)?|meanwhile|in\s+the\s+meantime|" +
        @"for\s+(?:next|this|the\s+next)\s+(?:week|month|quarter|year|time)|going\s+forward|from\s+now\s+on|in\s+the\s+future|" +
        @"back\s+(?:at|in|home)|at\s+(?:the\s+)?(?:office|school|home|work|meeting|event)|over\s+the\s+(?:weekend|holidays|summer)|" +
        @"a\s+(?:few|couple\s+of)\s+(?:days|weeks|months)\s+(?:ago|later)|in\s+(?:january|february|march|april|june|july|august|september|october|november|december))\b",
        Opts);
    // Turning to a request or question aimed at the reader.
    static readonly Regex Request = new(
        @"^(?:(?:also|and|so|oh),?\s+)?(?:can|could|would|will)\s+you\b|^(?:(?:also|and|so),?\s+)?please\b|^(?:would|is)\s+it\s+(?:be\s+)?possible\b|" +
        @"^let\s+me\s+know\b|^i\s+(?:need|would\s+like|'d\s+like)\s+you\s+to\b|^do\s+you\s+(?:mind|think|want|have)\b|^any\s+chance\b", Opts);
    // Words that point back to the previous sentence (cohesion by reference and conjunction).
    static readonly Regex PointsBack = new(
        @"^(?:it|its|it's|this|that|these|those|they|them|their|he|she|his|her|which|so|because|and|but|or|then|plus|otherwise|" +
        @"for\s+example|for\s+instance|in\s+fact|as\s+a\s+result|that's\s+why|which\s+means|even\s+then)\b", Opts);

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "so", "to", "of", "in", "on", "at", "by", "for", "with", "from", "about", "into",
        "is", "are", "was", "were", "be", "been", "being", "am", "do", "does", "did", "have", "has", "had", "will", "would",
        "can", "could", "should", "may", "might", "must", "shall", "i", "you", "he", "she", "it", "we", "they", "me", "him",
        "her", "us", "them", "my", "your", "his", "its", "our", "their", "this", "that", "these", "those", "there", "here",
        "what", "which", "who", "when", "where", "why", "how", "if", "then", "than", "not", "no", "yes", "just", "also",
        "very", "really", "too", "all", "any", "some", "more", "most", "much", "many", "get", "got", "go", "going", "make",
        "think", "know", "want", "like", "let", "lets", "i'm", "i'll", "i've", "i'd", "it's", "that's", "don't", "can't",
        "we're", "you're", "they're", "there's", "let's", "one", "thing", "things", "way", "still", "now", "well", "maybe",
        "need", "please", "thanks", "hi", "hey", "hello",
        // Common describing words that turn up across unrelated points, so they don't prove a connection.
        "new", "old", "same", "other", "every", "each", "last", "next", "first", "good", "great", "big", "small", "little",
        "about", "some", "few", "lot", "lots", "time", "day", "week", "today",
    };

    /// <summary>Splits text into paragraphs separated by a blank line. Single-sentence text is left as it is.</summary>
    public static string Format(string text, WritingStyle style)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var sentences = SentenceBreak.Split(text.Trim()).Where(s => s.Length > 0).ToList();
        var paragraphs = new List<List<string>>();

        // Introduction: "Hi John, I wanted to ask…" puts "Hi John," on its own line.
        if (style.GreetingAndSignOffLines && sentences.Count > 0 && Greeting.Match(sentences[0]) is { Success: true } g)
        {
            string rest = g.Groups["rest"].Value.Trim();
            paragraphs.Add([g.Groups["g"].Value.Trim()]);
            if (rest.Length > 0) sentences[0] = char.ToUpperInvariant(rest[0]) + rest[1..];
            else sentences.RemoveAt(0);
        }
        // Conclusion: a short sign-off at the end.
        string? signOff = null;
        if (style.GreetingAndSignOffLines && sentences.Count > 1 && sentences[^1].Length <= 60 && SignOff.IsMatch(sentences[^1]))
        {
            signOff = sentences[^1];
            sentences.RemoveAt(sentences.Count - 1);
        }

        double threshold = style.ParagraphEagerness switch { ParagraphEagerness.Fewer => 3.0, ParagraphEagerness.More => 1.5, _ => 2.0 };
        var words = sentences.Select(ContentWords).ToList();
        var current = new List<string>();
        int currentStart = 0;
        for (int i = 0; i < sentences.Count; i++)
        {
            if (current.Count > 0 && ShiftScore(sentences, words, i, currentStart, style) >= threshold)
            {
                paragraphs.Add(current);
                current = [];
                currentStart = i;
            }
            current.Add(sentences[i]);
        }
        if (current.Count > 0) paragraphs.Add(current);

        // The reader needs a pause: a long paragraph is split where its sentences are least connected.
        for (int p = 0; p < paragraphs.Count; p++)
        {
            // Paragraphs of 100-200 words are the usual guidance, so a pause is added past that.
            int wordCount = TextCleaner.CountWords(string.Join(" ", paragraphs[p]));
            if (!(paragraphs[p].Count >= 5 && wordCount > 100 || paragraphs[p].Count >= 4 && wordCount > 150)) continue;
            int cut = WeakestLink(paragraphs[p]);
            paragraphs.Insert(p + 1, paragraphs[p].Skip(cut).ToList());
            paragraphs[p] = paragraphs[p].Take(cut).ToList();
            p--; // the first half may still be long
        }

        if (signOff != null) paragraphs.Add([signOff]);
        return string.Join("\n\n", paragraphs.Select(p => string.Join(" ", p)));
    }

    /// <summary>How strongly sentence <paramref name="i"/> starts a new point. Around 2 or more starts a paragraph.</summary>
    public static double ShiftScore(IReadOnlyList<string> sentences, IReadOnlyList<HashSet<string>> words, int i, int paragraphStart, WritingStyle style)
    {
        string s = sentences[i].TrimStart('"', '“', '(');
        double score = 0;

        // Announced new point, contrast or wrap-up.
        bool starter = style.ParagraphStarters.Any(st => st.Trim().Length > 0
            && s.StartsWith(st.Trim(), StringComparison.OrdinalIgnoreCase)
            && (s.Length == st.Trim().Length || !char.IsLetterOrDigit(s[st.Trim().Length])));
        if (starter || Contrast.IsMatch(s) || WrapUp.IsMatch(s)) score += 3;
        else if (TimeOrPlace.IsMatch(s)) score += 2;

        // Turning from telling to asking.
        bool asking = Request.IsMatch(s);
        bool wasAsking = i > paragraphStart && Request.IsMatch(sentences[i - 1].TrimStart('"', '“', '('));
        if (asking && !wasAsking) score += 2;

        // A change of vocabulary: nothing in common with the last two sentences, and this sentence's words
        // carry on in the next one (a new point beginning, not a stray aside).
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int j = Math.Max(paragraphStart, i - 2); j < i; j++) before.UnionWith(words[j]);
        if (words[i].Count >= 2 && before.Count >= 2)
        {
            int shared = words[i].Count(before.Contains);
            if (shared == 0)
            {
                score += 1.5;
                if (i + 1 < sentences.Count && words[i + 1].Overlaps(words[i])) score += 0.5;
                // A new subject ("My car…" after sentences about the kitchen) confirms the shift.
                if (Subject(s) is { } subject && !before.Contains(subject)) score += 0.5;
            }
            else if (shared >= 2)
            {
                score -= 1; // clearly the same point
            }
        }

        // Words that point back keep the sentence with the one before it.
        if (PointsBack.IsMatch(s) && !Contrast.IsMatch(s)) score -= 1.5;
        // Avoid one-sentence paragraphs unless the signal is strong.
        if (i - paragraphStart == 1 && score < 3) score -= 1;
        return score;
    }

    /// <summary>The sentence's subject, roughly: its first meaningful word within the first four words.</summary>
    static string? Subject(string sentence)
    {
        foreach (Match m in Regex.Matches(sentence.ToLowerInvariant().Replace('’', '\''), @"[\p{L}\p{N}']+").Take(4))
        {
            string w = m.Value.Trim('\'');
            if (w.Length >= 3 && !Stop.Contains(w)) return Stem(w);
        }
        return null;
    }

    /// <summary>Where a long paragraph's sentences are least connected (TextTiling's deepest valley), not the middle.</summary>
    static int WeakestLink(List<string> sentences)
    {
        var words = sentences.Select(ContentWords).ToList();
        int best = sentences.Count / 2;
        double lowest = double.MaxValue;
        for (int i = 2; i <= sentences.Count - 2; i++) // both halves keep at least two sentences
        {
            var left = new HashSet<string>(words[i - 1], StringComparer.OrdinalIgnoreCase);
            left.UnionWith(words[i - 2]);
            var right = new HashSet<string>(words[i], StringComparer.OrdinalIgnoreCase);
            if (i + 1 < words.Count) right.UnionWith(words[i + 1]);
            double similarity = left.Count == 0 || right.Count == 0 ? 0
                : left.Count(right.Contains) / Math.Sqrt(left.Count * (double)right.Count);
            if (PointsBack.IsMatch(sentences[i])) similarity += 0.5;
            if (TimeOrPlace.IsMatch(sentences[i]) || Contrast.IsMatch(sentences[i]) || WrapUp.IsMatch(sentences[i])) similarity -= 0.3;
            if (similarity < lowest)
            {
                lowest = similarity;
                best = i;
            }
        }
        return best;
    }

    /// <summary>A sentence's meaningful words, lightly stemmed so "numbers" and "number" count as the same.</summary>
    public static HashSet<string> ContentWords(string sentence)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sentence.ToLowerInvariant().Replace('’', '\''), @"[\p{L}\p{N}']+"))
        {
            string w = m.Value.Trim('\'');
            if (w.Length < 3 || Stop.Contains(w)) continue;
            set.Add(Stem(w));
        }
        return set;
    }

    static string Stem(string w)
    {
        foreach (string suffix in new[] { "ing", "ies", "ed", "es", "s" })
            if (w.Length > suffix.Length + 3 && w.EndsWith(suffix, StringComparison.Ordinal))
                return suffix == "ies" ? w[..^3] + "y" : w[..^suffix.Length];
        return w;
    }
}
