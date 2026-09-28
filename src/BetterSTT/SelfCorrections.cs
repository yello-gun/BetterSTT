using System.Text.RegularExpressions;

namespace BetterSTT;

/// <summary>
/// Removes mid-sentence corrections: "meet on Tuesday, no wait, Wednesday" → "meet on Wednesday".
/// A cue like "no wait" or "scratch that" only marks a possible correction. What it replaces is worked
/// out from context, by lining up what was said after the cue with what came before it:
/// a repeated word ("the table, no wait, the shelf"), a word of the same kind (a day, number, time,
/// color or name), a restarted clause ("we should, no wait, let's cancel"), or the last few words.
/// When nothing lines up, a weak cue ("sorry", "I mean") is treated as ordinary speech and left alone.
/// </summary>
public static class SelfCorrections
{
    enum Strength { Weak, Medium, Strong }

    // Strong cues always correct something; weak ones only when the context clearly lines up.
    const string StrongCues = @"scratch\s+that|strike\s+that|delete\s+that|forget\s+that|let\s+me\s+rephrase(?:\s+that)?|let\s+me\s+start\s+over|start\s+over";
    const string MediumCues = @"no,?\s+wait|wait,?\s+no|no,?\s+no(?:,?\s+no)?|actually,?\s+no|no,?\s+actually|or\s+rather|make\s+that|correction|i\s+meant|sorry,?\s+i\s+mean|i\s+mean,?\s+no|oops|my\s+bad";
    const string WeakCues = @"sorry|i\s+mean|wait|actually|rather";

    static readonly Regex Cue = new(
        $@"(?<pre>^|[,.;:!?—–]|\s-{{1,2}}(?=\s))(?<fill>(?:\s*(?:u+m+|u+h+|e+r+m*|h+m+|a+h+)[,.]?)*)\s*(?:(?<s>{StrongCues})|(?<m>{MediumCues})|(?<w>{WeakCues}))(?![\p{{L}}\p{{N}}'’])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex AfterBoundary = new(@"^\s*(?:[,.;:!?—–]|-{1,2}\s|$)");
    static readonly Regex LeadingJunk = new(@"^(?:[\s,.;:!?—–-]|\b(?:u+m+|u+h+|e+r+m*|h+m+|a+h+)\b)+", RegexOptions.IgnoreCase);
    static readonly Regex TrailingJunk = new(@"(?:[\s,;:—–-]|\b(?:u+m+|u+h+|e+r+m*|h+m+|a+h+)\b)+$", RegexOptions.IgnoreCase);
    static readonly Regex SentenceEnd = new(@"[.!?](?=\s|$)");

    static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "on", "at", "in", "by", "for", "with", "of", "from", "into", "about", "my", "your",
        "our", "their", "his", "her", "its", "this", "that", "these", "those", "some", "is", "are", "was", "be",
    };
    static readonly HashSet<string> ClauseStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "i'm", "i'll", "i've", "i'd", "we", "we're", "we'll", "you", "you're", "he", "she", "they", "it", "it's",
        "let's", "let", "maybe", "can", "could", "should", "would", "will", "please", "there", "there's", "just", "how",
        "what", "why", "when", "where", "do", "don't", "does",
    };
    static readonly HashSet<string> Weekdays = new(StringComparer.OrdinalIgnoreCase)
        { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };
    static readonly HashSet<string> Months = new(StringComparer.OrdinalIgnoreCase)
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
    static readonly HashSet<string> Times = new(StringComparer.OrdinalIgnoreCase)
        { "today", "tomorrow", "tonight", "yesterday", "morning", "afternoon", "evening", "noon", "midnight", "weekend" };
    static readonly HashSet<string> Colors = new(StringComparer.OrdinalIgnoreCase)
        { "red", "orange", "yellow", "green", "blue", "purple", "pink", "black", "white", "gray", "grey", "brown" };
    static readonly HashSet<string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
        "fifteen", "twenty", "thirty", "forty", "fifty", "hundred", "thousand", "million", "half", "dozen",
        "first", "second", "third", "fourth", "fifth", "last",
    };

    enum Kind { None, Number, Weekday, Month, Time, Color, Name }

    /// <summary>Applies every correction in the text, left to right.</summary>
    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        int searchFrom = 0;
        for (int guard = 0; guard < 12 && searchFrom <= text.Length; guard++)
        {
            var m = Cue.Match(text, searchFrom);
            if (!m.Success) break;
            string? fixedText = TryCorrect(text, m);
            if (fixedText == null)
            {
                searchFrom = m.Index + m.Length; // ordinary speech; look further on
                continue;
            }
            text = fixedText;
            searchFrom = 0;
        }
        return text;
    }

    static string? TryCorrect(string text, Match m)
    {
        var strength = m.Groups["s"].Success ? Strength.Strong : m.Groups["m"].Success ? Strength.Medium : Strength.Weak;
        string after = text[(m.Index + m.Length)..];
        if (strength == Strength.Weak && !AfterBoundary.IsMatch(after)) return null; // "I mean it", "wait for me"

        // What was said before the cue, and the sentence (or, after a full stop, the previous sentence) it corrects.
        int preEnd = m.Groups["pre"].Index;
        string before = text[..(m.Groups["pre"].Value is "." or "!" or "?" ? preEnd + 1 : preEnd)];
        before = TrailingJunk.Replace(before, "");
        if (before.Trim().Length == 0)
        {
            // Nothing to correct: "Sorry, I can't make it." stays; a stray "Scratch that" is dropped.
            return strength == Strength.Strong ? Capitalize(LeadingJunk.Replace(after, "")) : null;
        }

        string trimmedBefore = before.TrimEnd();
        bool afterFullStop = trimmedBefore.Length > 0 && ".!?".Contains(trimmedBefore[^1]);
        string body = afterFullStop ? trimmedBefore[..^1] : trimmedBefore;
        int scopeStart = 0;
        foreach (Match end in SentenceEnd.Matches(body)) scopeStart = end.Index + 1;
        string prefix = body[..scopeStart];
        string[] scope = body[scopeStart..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // The replacement: from the cue to the end of its sentence.
        string rest = LeadingJunk.Replace(after, "");
        var endMatch = SentenceEnd.Match(rest);
        string repair = endMatch.Success ? rest[..endMatch.Index] : rest;
        string tail = endMatch.Success ? rest[endMatch.Index..] : "";
        repair = TrailingJunk.Replace(repair, "").Trim();
        // "Tuesday, no wait, Wednesday, to go over the plan": the fix is "Wednesday" and the sentence carries
        // on after it, without the comma that only marked the pause.
        string continuation = "";
        int comma = repair.IndexOf(", ", StringComparison.Ordinal);
        if (comma > 0)
        {
            continuation = repair[(comma + 2)..];
            repair = repair[..comma];
        }
        string[] fix = repair.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fix.Length == 0)
        {
            if (strength != Strength.Strong) return null;
            // "…, scratch that." drops what came before it.
            return Join(prefix, "", LeadingJunk.Replace(tail, ""));
        }
        if (scope.Length == 0) return null;

        bool sentenceStartedBeforeFix = afterFullStop || m.Groups["pre"].Value is "." or "!" or "?";
        if (sentenceStartedBeforeFix) fix[0] = LowerIfCommon(fix[0]);

        // "…MLA? Scratch that. The syllabus says APA." A strong cue said as its own sentence takes back the
        // whole previous sentence; mid-sentence ("Tuesday, scratch that, Wednesday") it's lined up like the others.
        List<string>? result;
        if (strength == Strength.Strong && afterFullStop)
        {
            result = [.. fix];
        }
        else
        {
            // First the sentence the correction is in, then anything said earlier, however long ago: in
            // "Meet on Tuesday at the café. I'll bring the slides. No wait, Wednesday." it's Tuesday that changes.
            result = Align(scope, fix, strength, lineUpOnly: true);
            if (result == null && FixEarlier(prefix, fix, strength) is { } fixedPrefix)
            {
                // That earlier sentence is corrected; this one stays as it was, minus the correction.
                string kept = string.Join(' ', scope) + (afterFullStop ? trimmedBefore[^1].ToString() : "");
                if (continuation.Length > 0 && !afterFullStop) kept += " " + continuation;
                return Join(fixedPrefix, Capitalize(kept), afterFullStop ? tail.TrimStart('.', '!', '?') : tail);
            }
            result ??= Align(scope, fix, strength, lineUpOnly: false);
        }
        if (result == null) return null;
        if (continuation.Length > 0) result.Add(continuation);
        string sentence = Capitalize(string.Join(' ', result));
        return Join(prefix, sentence, tail);
    }

    /// <summary>
    /// Works out which part of <paramref name="scope"/> the fix replaces; null if nothing lines up.
    /// With <paramref name="lineUpOnly"/>, only a repeated word or a word of the same kind counts (the weaker
    /// guesses, a restarted clause or the last few words, come after looking at earlier sentences).
    /// </summary>
    static List<string>? Align(string[] scope, string[] fix, Strength strength, bool lineUpOnly = false)
    {
        // 1. The fix repeats an earlier word and carries on from there: "the table, no wait, the shelf".
        // After a weak cue a repeated little word ("the") is too common to prove anything.
        string first = Norm(fix[0]);
        if (strength != Strength.Weak || !Stopwords.Contains(first))
        {
            for (int i = scope.Length - 1; i >= 0; i--)
                if (Norm(scope[i]) == first) return [.. scope[..i], .. fix];
        }

        // 2. A word of the same kind: a day, number, time, color or name.
        int k = Array.FindIndex(fix, w => !Stopwords.Contains(Norm(w)));
        if (k >= 0 && KindOf(fix[k], isFirstInSentence: false) is var kind and not Kind.None)
        {
            for (int i = scope.Length - 1; i >= 0; i--)
            {
                if (KindOf(scope[i], isFirstInSentence: i == 0) != kind) continue;
                if (fix.Length - k == 1)
                {
                    // Only that word changes: "Tuesday at 3, no wait, Wednesday" → "Wednesday at 3".
                    var swapped = scope.ToList();
                    swapped[i] = fix[k] + TrailingPunctuation(scope[i]);
                    if (k > 0) swapped.InsertRange(i, fix[..k].Where(w => i == 0 || Norm(scope[i - 1]) != Norm(w)));
                    return swapped;
                }
                return [.. scope[..i], .. fix[k..]];
            }
        }

        if (strength == Strength.Weak || lineUpOnly) return null;

        // 3. A restarted clause: "I think we should, no wait, let's just cancel".
        if (ClauseStarters.Contains(first))
        {
            int start = 0;
            for (int i = scope.Length - 1; i > 0; i--)
            {
                if (scope[i - 1].EndsWith(',') || Norm(scope[i]) is "and" or "but" or "so" or "then")
                {
                    start = Norm(scope[i]) is "and" or "but" or "so" or "then" ? i + 1 : i;
                    break;
                }
            }
            if (strength == Strength.Strong) start = 0;
            return [.. scope[..start], .. fix];
        }

        // 4. The fix replaces the last few words: "it's big, no wait, huge", "I want pizza, no wait, a burger".
        int content = fix.Count(w => !Stopwords.Contains(Norm(w)));
        if (content is >= 1 and <= 3 && content < scope.Length)
        {
            int idx = scope.Length, seen = 0;
            while (idx > 0 && seen < content)
            {
                idx--;
                if (!Stopwords.Contains(Norm(scope[idx]))) seen++;
            }
            return [.. scope[..idx], .. fix];
        }

        // A strong cue ("scratch that") with nothing that lines up replaces the whole sentence.
        return strength == Strength.Strong ? [.. fix] : null;
    }

    /// <summary>
    /// Looks back through the sentences before this one, nearest first, for a word of the same kind as the fix
    /// (a day, number, time, color or name) and replaces it there. Returns the corrected earlier text, or null.
    /// </summary>
    static string? FixEarlier(string earlier, string[] fix, Strength strength)
    {
        // Only a clear correction cue and a short fix ("no wait, Wednesday"), so ordinary speech isn't rewritten.
        if (strength == Strength.Weak || fix.Length > 4 || earlier.Trim().Length == 0) return null;
        int k = Array.FindIndex(fix, w => !Stopwords.Contains(Norm(w)));
        if (k < 0) return null;
        var kind = KindOf(fix[k], isFirstInSentence: false);
        if (kind == Kind.None) return null;

        var starts = new List<int> { 0 };
        foreach (Match end in SentenceEnd.Matches(earlier)) starts.Add(end.Index + 1);
        for (int sIndex = starts.Count - 1; sIndex >= 0; sIndex--)
        {
            int start = starts[sIndex], stop = sIndex + 1 < starts.Count ? starts[sIndex + 1] : earlier.Length;
            string[] tokens = earlier[start..stop].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                if (KindOf(tokens[i], isFirstInSentence: i == 0) != kind) continue;
                // Replace as many words as the fix lines up with: "3 pm" → "4 pm", or just "Tuesday" → "Wednesday".
                int span = 1;
                while (i + span < tokens.Length && k + span < fix.Length
                       && (Norm(tokens[i + span]) == Norm(fix[k + span])
                           || KindOf(fix[k + span], false) is var nextKind && nextKind != Kind.None && KindOf(tokens[i + span], false) == nextKind))
                    span++;
                string punctuation = Regex.Match(tokens[i + span - 1], @"[,.;:!?]+$").Value;
                string replacement = string.Join(' ', fix[k..]) + punctuation;
                var rebuilt = tokens[..i].Append(replacement).Concat(tokens[(i + span)..]);
                return (earlier[..start] + " " + string.Join(' ', rebuilt) + " " + earlier[stop..]).Trim();
            }
        }
        return null;
    }

    static Kind KindOf(string word, bool isFirstInSentence)
    {
        string w = Norm(word);
        if (w.Length == 0) return Kind.None;
        if (char.IsDigit(w[0]) || NumberWords.Contains(w) || Regex.IsMatch(w, @"^\d+(?:st|nd|rd|th)$")) return Kind.Number;
        if (Weekdays.Contains(w)) return Kind.Weekday;
        if (Months.Contains(w) && w != "may") return Kind.Month;
        if (Times.Contains(w)) return Kind.Time;
        if (Colors.Contains(w)) return Kind.Color;
        string bare = word.Trim(',', '.', ';', ':', '!', '?', '"', '“', '”');
        if (!isFirstInSentence && bare.Length > 1 && char.IsUpper(bare[0]) && bare != "I"
            && !bare.StartsWith("I'", StringComparison.Ordinal) && !ClauseStarters.Contains(bare) && !Stopwords.Contains(bare))
            return Kind.Name;
        return Kind.None;
    }

    static string Norm(string word) => Regex.Replace(word.ToLowerInvariant(), @"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$", "").Replace('’', '\'');

    static string TrailingPunctuation(string word) => Regex.Match(word, @"[,;:]+$").Value;

    /// <summary>Speech recognition capitalizes the first word after "No, wait." even mid-sentence.</summary>
    static string LowerIfCommon(string word)
    {
        string n = Norm(word);
        bool common = Stopwords.Contains(n) || NumberWords.Contains(n) || Colors.Contains(n) || Times.Contains(n)
                      || (ClauseStarters.Contains(n) && n != "i" && !n.StartsWith("i'"));
        return common && word.Length > 0 && char.IsUpper(word[0]) ? char.ToLowerInvariant(word[0]) + word[1..] : word;
    }

    static string Capitalize(string s) =>
        s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    static string Join(string prefix, string sentence, string tail)
    {
        prefix = prefix.TrimEnd();
        string joined = prefix.Length == 0 ? sentence : sentence.Length == 0 ? prefix : prefix + " " + sentence;
        if (sentence.Length == 0) tail = tail.TrimStart('.', '!', '?', ',', ' ');
        string result = (joined + tail).Trim();
        return Regex.Replace(result, @"\s{2,}", " ");
    }
}
