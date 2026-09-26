using System.Text.RegularExpressions;

namespace BetterTTS;

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

    public static string Clean(string text, CleanupOptions o)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string s = text.Replace(Cap.ToString(), "").Replace(Pause.ToString(), "").Replace("…", "...");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (!o.Enabled) return s;

        if (o.RemoveNonSpeechTags) s = NonSpeechTags.Replace(s, " ");
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

    static string RemoveStutters(string s, IEnumerable<string> exceptions)
    {
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
