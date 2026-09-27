using System.Text.RegularExpressions;

namespace BetterSTT;

/// <summary>
/// Removes filler sounds, pause ellipses, stutters and verbal fillers from a transcript
/// while keeping punctuation and capitalization tidy.
/// </summary>
public static class TextCleaner
{
    // Internal marker meaning "capitalize the next letter" (used when a sentence-opening filler is removed).
    const char Cap = '\u0001';
    // Internal placeholder for a "..." pause. It acts as a clause boundary while fillers are removed.
    const char Pause = '\u0002';
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string WordChar = @"[\p{L}\p{N}'’-]";

    static readonly Regex NonSpeechTags = new(
        @"\[[^\]]*\]|\*[^*\n]{1,40}\*|\((?=[^)]*\b(?:music|silence|inaudible|laugh\w*|applause|cough\w*|sigh\w*|breath\w*|noise|static|blank)\b)[^)]*\)",
        Opts);
    static readonly Regex Ellipsis = new(@"\s*(?:\.[ ]?){2,}\s*", Opts);
    static readonly Regex Stutter = new(
        @"(?<![\p{L}'’])(?<w>\p{L}+)(?:(?:\s*,\s*|\s*-\s*|\s+)\k<w>(?![\p{L}'’]))+", Opts);
    // A repeated run of 2–4 words: "I think I think we should".
    static readonly Regex PhraseStutter = new(
        @"(?<![\p{L}'’])(?<p>\p{L}+(?:['’]\p{L}+)?(?:\s+\p{L}+(?:['’]\p{L}+)?){1,3})(?:[\s,]+\k<p>(?![\p{L}'’]))+", Opts);
    // A word cut off and restarted: "we were go- going", "it's a big—bigger problem". A plain hyphen
    // without a following space is left alone, so real words like "re-read" are safe.
    static readonly Regex CutOffWord = new(
        @"(?<![\p{L}'’])(?<f>\p{L}+)(?:-\s+|\s*(?:—|–|--)\s*)(?=(?<n>\p{L}+))", Opts);
    static readonly Regex SentenceBreak = new(@"(?<=[.!?][""”’)]?)\s+(?=[\p{Lu}\p{N}""“(])");
    static readonly Regex Greeting = new(
        @"^(?<g>(?:hi|hello|hey|dear|greetings|good\s+(?:morning|afternoon|evening))\b[^,.!?]{0,40}[,!])\s*(?<rest>.*)$", Opts);
    static readonly Regex SignOff = new(
        @"^(?:thanks|thank\s+you|many\s+thanks|best|best\s+regards|kind\s+regards|regards|cheers|sincerely|talk\s+(?:soon|to\s+you\s+soon)|see\s+you(?:\s+soon)?|take\s+care)\b", Opts);

    public static string Clean(string text, CleanupOptions o)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string s = text.Replace(Cap.ToString(), "").Replace(Pause.ToString(), "").Replace("…", "...");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (!o.Enabled) return s;

        if (o.RemoveNonSpeechTags) s = NonSpeechTags.Replace(s, " ");
        if (o.FixCorrections) s = SelfCorrections.Apply(Regex.Replace(s, @"\s+", " ").Trim());
        if (o.RemoveEllipses) s = Ellipsis.Replace(s, $" {Pause} ");

        var discourse = o.RemoveDiscourseMarkers ? BuildWordRegex(o.DiscourseMarkers, elongate: false) : null;
        var fillers = o.RemoveFillerSounds ? BuildWordRegex(o.FillerSounds, elongate: true) : null;
        for (int pass = 0; pass < 4; pass++)
        {
            string before = s;
            if (discourse != null) s = RemoveWords(s, discourse, requireBoundaries: true);
            if (fillers != null) s = RemoveWords(s, fillers, requireBoundaries: false);
            if (s == before) break;
        }

        if (o.RemoveEllipses) s = ResolvePauses(s);
        if (o.RemoveStutters) s = RemoveStutters(s, o.StutterExceptions);
        return Tidy(s);
    }

    /// <summary>
    /// The full text pipeline: cleanup, the style's extra trimming, word fixes, spoken math, then paragraphs.
    /// The Exact words style (or cleanup switched off) returns the transcript as heard. A dictation that is
    /// just a snippet's trigger becomes the snippet's text, in every style.
    /// </summary>
    public static string Process(string raw, CleanupOptions cleanup, IReadOnlyList<Replacement> replacements,
        WritingStyle? style = null, IReadOnlyList<Snippet>? snippets = null)
    {
        if (snippets is { Count: > 0 } && (MatchSnippet(Clean(raw, cleanup), snippets) ?? MatchSnippet(raw, snippets)) is { } snippet)
            return snippet;

        style ??= WritingStyle.Natural();
        if (style.ExactWords || !cleanup.Enabled)
            return string.IsNullOrWhiteSpace(raw) ? "" : Regex.Replace(raw, @"\s+", " ").Trim();

        string s = Clean(raw, cleanup);
        if (style.RemoveVagueEndings) s = RemoveVagueEndings(s, style.VagueEndings);
        s = ApplyReplacements(s, replacements);
        s = SpokenMath.Convert(s, style.SpokenMath);
        if (style.SpokenCode) s = SpokenCode.Convert(s);
        if (style.SpokenPunctuation) s = ApplySpokenPunctuation(s);

        // Spoken "new line" / "new paragraph" split the text into blocks; automatic paragraphs work within each.
        var blocks = style.SpokenLayout ? SplitSpokenLayout(s) : [(s, "")];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < blocks.Count; i++)
        {
            var (block, separator) = blocks[i];
            string b = block.Trim();
            // A block after a spoken break starts a new sentence.
            if (i > 0 && b.Length > 0 && char.IsLower(b[0])) b = char.ToUpperInvariant(b[0]) + b[1..];
            if (style.AutoParagraphs) b = FormatParagraphs(b, style);
            sb.Append(b).Append(separator);
        }
        return sb.ToString().Trim(' ', '\n');
    }

    static readonly Regex SpokenLayoutCommand = new(
        @"(?<pre>^|[.!?,;:])\s*\bnew\s+(?<k>line|paragraph)\b(?:\s*[.,!?;:]|\s*$|(?=\s+(?-i:\p{Lu})))\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits text at spoken "new line" / "new paragraph" commands (only when said as their own phrase,
    /// so "add a new line to the file" is left alone). Each block comes with the break that follows it.
    /// </summary>
    public static List<(string Text, string Separator)> SplitSpokenLayout(string text)
    {
        var blocks = new List<(string, string)>();
        int last = 0;
        foreach (Match m in SpokenLayoutCommand.Matches(text))
        {
            string block = text[last..m.Index];
            string pre = m.Groups["pre"].Value;
            // A comma before the break ends the paragraph, except after a greeting ("Hi Sarah,").
            if (pre == ",") pre = Greeting.IsMatch(block.Trim() + ",") ? "," : ".";
            if (block.Trim().Length > 0) block = block.TrimEnd() + pre;
            string separator = m.Groups["k"].Value.Equals("line", StringComparison.OrdinalIgnoreCase) ? "\n" : "\n\n";
            if (block.Trim().Length > 0) blocks.Add((block, separator));
            last = m.Index + m.Length;
        }
        blocks.Add((text[last..], ""));
        return blocks;
    }

    static readonly (Regex Pattern, string Mark)[] SpokenMarks =
    [
        (new(@"[\s,]*\b(?:question\s+mark)\b[.,?]?", Opts), "?"),
        (new(@"[\s,]*\b(?:exclamation\s+(?:mark|point))\b[.,!]?", Opts), "!"),
        (new(@"[\s,]*\b(?:full\s+stop|period)\b[.,]?(?=\s*$|\s*[\p{P}]|\s+(?-i:\p{Lu})|\s+new\b)", Opts), "."),
        (new(@"[\s,]*\bsemicolon\b[.,;]?", Opts), ";"),
        (new(@"[\s,]*\bcolon\b[.,:]?", Opts), ":"),
        (new(@"[\s,]*\bcomma\b[.,]?", Opts), ","),
        (new(@"\s*\b(?:open|begin)\s+quotes?\b[.,]?\s*", Opts), " \u201C"),
        (new(@"[\s,]*\b(?:(?:close|end)\s+quotes?|unquote)\b", Opts), "\u201D"),
    ];

    /// <summary>"Hello comma how are you question mark" → "Hello, how are you?"</summary>
    public static string ApplySpokenPunctuation(string text)
    {
        foreach (var (pattern, mark) in SpokenMarks) text = pattern.Replace(text, mark);
        text = Regex.Replace(text, @"([,;:])(?=[\p{L}\p{N}])", "$1 ");
        text = Regex.Replace(text, @"([.!?])\s+(\p{Ll})", m => m.Groups[1].Value + " " + char.ToUpperInvariant(m.Groups[2].Value[0]));
        text = Regex.Replace(text, @"([.!?])([.,])", "$1");
        text = Regex.Replace(text, @"([?!,;:])\s*\1", "$1"); // "you? Question mark." gave "you??"
        return Regex.Replace(text, @" {2,}", " ").Trim();
    }

    /// <summary>The same pipeline with everything taken from the settings.</summary>
    public static string Process(string raw, AppSettings s) =>
        Process(raw, s.Cleanup, s.Replacements, s.CurrentStyle, s.Snippets);

    /// <summary>
    /// The text of the snippet whose trigger is the whole of <paramref name="text"/>, ignoring case and
    /// punctuation ("My email." matches "my email"), or null.
    /// </summary>
    public static string? MatchSnippet(string text, IReadOnlyList<Snippet> snippets)
    {
        static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        string key = Key(text);
        if (key.Length == 0) return null;
        var hit = snippets.FirstOrDefault(s => s.Text.Trim().Length > 0 && Key(s.Trigger) == key);
        return hit?.Text.Replace("\r\n", "\n").Trim();
    }

    /// <summary>
    /// Removes vague tails that carry no information when they end a clause:
    /// "we could grab lunch or something, then head back" → "we could grab lunch, then head back".
    /// </summary>
    public static string RemoveVagueEndings(string text, IReadOnlyList<string> endings)
    {
        var alts = endings
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(e => e.Length)
            .Select(e => string.Join(@"\s+", e.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)))
            .ToList();
        if (alts.Count == 0 || string.IsNullOrEmpty(text)) return text;
        var rx = new Regex($@"(?:\s*,)?\s+(?:{string.Join("|", alts)})(?![\p{{L}}\p{{N}}])(?=\s*(?:[.,!?;:]|$))", Opts);
        return Tidy(rx.Replace(text, ""));
    }

    /// <summary>
    /// Splits text into paragraphs separated by a blank line. A new paragraph starts at a change of
    /// topic ("Also…", "Next…"), after a greeting, before a sign-off, and after the style's maximum
    /// number of sentences. Single-sentence text is left as it is.
    /// </summary>
    public static string FormatParagraphs(string text, WritingStyle style)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var sentences = SentenceBreak.Split(text.Trim()).Where(s => s.Length > 0).ToList();

        var paragraphs = new List<List<string>>();
        var current = new List<string>();
        void Break()
        {
            if (current.Count > 0) paragraphs.Add(current);
            current = new List<string>();
        }

        // "Hi John, I wanted to ask…" → "Hi John," on its own line, then the rest.
        if (style.GreetingAndSignOffLines && sentences.Count > 0 && Greeting.Match(sentences[0]) is { Success: true } g)
        {
            string rest = g.Groups["rest"].Value.Trim();
            current.Add(g.Groups["g"].Value.Trim());
            Break();
            if (rest.Length > 0) sentences[0] = char.ToUpperInvariant(rest[0]) + rest[1..];
            else sentences.RemoveAt(0);
        }

        var starters = style.ParagraphStarters
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        int max = Math.Max(1, style.MaxSentencesPerParagraph);

        for (int i = 0; i < sentences.Count; i++)
        {
            string sentence = sentences[i];
            bool last = i == sentences.Count - 1;
            bool signOff = style.GreetingAndSignOffLines && last && sentences.Count > 1 && sentence.Length <= 60 && SignOff.IsMatch(sentence);
            bool topicChange = starters.Any(st =>
                sentence.StartsWith(st, StringComparison.OrdinalIgnoreCase)
                && (sentence.Length == st.Length || !char.IsLetterOrDigit(sentence[st.Length])));

            if (current.Count > 0 && (signOff || topicChange || current.Count >= max)) Break();
            current.Add(sentence);
        }
        Break();

        return string.Join("\n\n", paragraphs.Select(p => string.Join(" ", p)));
    }

    /// <summary>
    /// Replaces whole words or phrases, case-insensitively. All fixes run in a single pass, so one
    /// fix's output is never rewritten by another.
    /// </summary>
    public static string ApplyReplacements(string text, IReadOnlyList<Replacement> replacements)
    {
        // Spaces and hyphens are interchangeable, so "git hub", "git-hub" and "git  hub" all match.
        static string Key(string s) => Regex.Replace(s.Trim(), @"[\s-]+", " ");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in replacements)
        {
            string from = Key(r.From);
            if (from.Length > 0 && !map.ContainsKey(from)) map[from] = r.To.Trim();
        }
        if (map.Count == 0 || string.IsNullOrEmpty(text)) return text;

        // Longest first, so "git hub desktop" wins over "git hub".
        string alternation = string.Join("|", map.Keys
            .OrderByDescending(k => k.Length)
            .Select(k => string.Join(@"[\s-]+", k.Split(' ').Select(Regex.Escape))));
        var rx = new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{alternation})(?![\p{{L}}\p{{N}}])", Opts);
        string result = rx.Replace(text, m => map.TryGetValue(Key(m.Value), out var to) ? to : m.Value);
        return Regex.Replace(result, @" {2,}", " ").Trim();
    }

    /// <summary>Phrases Whisper is known to invent from near-silence or background noise.</summary>
    static readonly HashSet<string> Phantoms = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thank you very much", "thank you so much", "thanks", "thanks for watching",
        "thank you for watching", "thanks for listening", "thank you for listening", "bye", "bye bye", "you",
        "please subscribe", "subscribe", "i'm sorry", "okay", "oh", "so", "the end", "subtitles by the amara org community",
    };

    /// <summary>
    /// True when the whole transcript is a phrase Whisper tends to invent and the audio held almost no
    /// clear speech, e.g. "Thank you." from a quiet room. Said deliberately, the same words are kept.
    /// </summary>
    public static bool IsLikelyPhantom(string raw, double loudSeconds)
    {
        string key = Regex.Replace(raw, @"\[[^\]]*\]|\([^)]*\)", " ").ToLowerInvariant().Replace('’', '\'');
        key = Regex.Replace(key, @"[^\p{L}\p{N}' ]+", " ").Trim();
        key = Regex.Replace(key, @"\s+", " ");
        return (key.Length == 0 || Phantoms.Contains(key)) && loudSeconds < 0.35;
    }

    public static int CountWords(string s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : Regex.Matches(s, @"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*").Count;

    /// <summary>
    /// Turns each pause placeholder into nothing (mid-sentence), a period (end of text, or before a new
    /// capitalized sentence), or a capitalize-marker (start of text).
    /// </summary>
    static string ResolvePauses(string s) => Regex.Replace(s, $@" *{Pause}[ {Pause}]*", m =>
    {
        int end = m.Index + m.Length;
        if (s.AsSpan(0, m.Index).Trim(' ').Trim(Cap).IsEmpty) return " " + Cap;
        if (end >= s.Length) return ".";
        bool pronounI = s[end] == 'I' && (end + 1 >= s.Length || !char.IsLetter(s[end + 1]));
        return char.IsUpper(s[end]) && !pronounI ? ". " : " ";
    });

    static Regex? BuildWordRegex(IEnumerable<string> words, bool elongate)
    {
        var alts = words
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(w => w.Length)
            .Select(w =>
            {
                string p = string.Join(@"\s+", w.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
                // "um" also matches "ummm", "hmm" matches "hmmmm".
                return elongate && w.All(char.IsLetter) ? p + "+" : p;
            })
            .ToList();
        if (alts.Count == 0) return null;
        return new Regex(
            $@"(?<pre>,\s*)?(?<!{WordChar})(?:{string.Join("|", alts)})(?!{WordChar})(?<post>\s*[,.!?]+)?", Opts);
    }

    /// <param name="requireBoundaries">
    /// When true (verbal fillers like "like" / "you know"), a phrase is only removed if it stands alone as its
    /// own clause, e.g. "It was, like, huge" or "You know, I think". "I like pizza" is left alone.
    /// </param>
    static string RemoveWords(string s, Regex rx, bool requireBoundaries) => rx.Replace(s, m =>
    {
        bool hasPre = m.Groups["pre"].Success;
        string post = m.Groups["post"].Success ? m.Groups["post"].Value.Trim() : "";
        bool sentenceStart = !hasPre && IsSentenceStart(s, m.Index);
        bool boundaryBefore = hasPre || PreviousChar(s, m.Index) == Pause;
        var rest = s.AsSpan(m.Index + m.Length).TrimStart(' ');
        bool boundaryAfter = post.Length > 0 || rest.IsEmpty || rest[0] == Pause;

        if (requireBoundaries && !((boundaryBefore || sentenceStart) && boundaryAfter))
            return m.Value;

        string endPunct = post.Replace(",", "");
        if (endPunct.Length > 0) return sentenceStart ? " " : endPunct + " ";
        return sentenceStart ? " " + Cap : " ";
    });

    static bool IsSentenceStart(string s, int index)
    {
        char c = PreviousChar(s, index);
        return c is '\0' or '.' or '!' or '?';
    }

    /// <summary>The nearest character before <paramref name="index"/> that isn't a space or marker; '\0' at the start.</summary>
    static char PreviousChar(string s, int index)
    {
        for (int i = index - 1; i >= 0; i--)
            if (s[i] != ' ' && s[i] != Cap) return s[i];
        return '\0';
    }

    /// <summary>Cut-off words, repeated phrases, then repeated words (minus the allowed exceptions).</summary>
    static string RemoveStutters(string s, IEnumerable<string> exceptions)
    {
        s = CutOffWord.Replace(s, m =>
        {
            string fragment = m.Groups["f"].Value, next = m.Groups["n"].Value;
            bool restarted = next.Length > fragment.Length && next.StartsWith(fragment, StringComparison.OrdinalIgnoreCase);
            return restarted ? "" : m.Value;
        });
        s = PhraseStutter.Replace(s, m => m.Groups["p"].Value);
        var keep = new HashSet<string>(exceptions.Select(e => e.Trim()), StringComparer.OrdinalIgnoreCase);
        return Stutter.Replace(s, m => keep.Contains(m.Groups["w"].Value) ? m.Value : m.Groups["w"].Value);
    }

    static string Tidy(string s)
    {
        s = Regex.Replace(s, @"\s+", " ");
        s = Regex.Replace(s, @" +([,.!?;:])", "$1");
        s = Regex.Replace(s, @",(?:[ \u0001]*,)+", ",");
        s = Regex.Replace(s, @",[ \u0001]*([.!?])", "$1");
        s = Regex.Replace(s, @"([.!?])[ \u0001]*,", "$1");
        s = Regex.Replace(s, @"([!?])\.+", "$1");
        s = Regex.Replace(s, @"(?<!\.)\.\.(?!\.)", ".");

        // Strip punctuation left dangling at the start; if anything was removed there, capitalize what follows.
        int i = 0;
        bool capitalize = false;
        while (i < s.Length && (s[i] == ' ' || s[i] == Cap || ",.;:!?".Contains(s[i])))
        {
            if (s[i] != ' ') capitalize = true;
            i++;
        }
        s = (capitalize ? Cap.ToString() : "") + s[i..];

        s = Regex.Replace(s, "\u0001+([ \u0001\"'(]*)(\\p{Ll})",
            m => m.Groups[1].Value + char.ToUpperInvariant(m.Groups[2].Value[0]));
        s = s.Replace(Cap.ToString(), "");
        s = Regex.Replace(s, @" {2,}", " ");
        s = Regex.Replace(s, @" +([,.!?;:])", "$1");
        s = Regex.Replace(s.Trim(), @"[,;:]+$", "");
        return s;
    }
}
