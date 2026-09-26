using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterSTT;

public sealed class RecentDictation
{
    public DateTime Time { get; set; }
    public string Raw { get; set; } = "";
    public string Clean { get; set; } = "";
    public int WordsRemoved { get; set; }
}

/// <summary>Lifetime stats plus the last few dictations, stored locally in history.json.</summary>
public sealed class HistoryStore
{
    public const int MaxRecent = 3;
    static string FilePath => Path.Combine(AppPaths.Data, "history.json");

    public int Dictations { get; set; }
    public int WordsRemoved { get; set; }
    public List<RecentDictation> Recent { get; set; } = new();

    /// <summary>False for the sample history used in screenshots, which must never touch disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Persist { get; init; } = true;

    public static HistoryStore Sample() => new()
    {
        Persist = false,
        Dictations = 142,
        WordsRemoved = 1284,
        Recent =
        [
            new() { Time = DateTime.Now.AddMinutes(-2), WordsRemoved = 4,
                Raw = "Um, so I was thinking, uh, that we should, like, try the the new approach.",
                Clean = "So I was thinking that we should try the new approach." },
            new() { Time = DateTime.Now.AddMinutes(-18), WordsRemoved = 3,
                Raw = "Hmm. Can you, you know, rewrite this function to use async?",
                Clean = "Can you rewrite this function to use async?" },
            new() { Time = DateTime.Now.AddHours(-1), WordsRemoved = 1,
                Raw = "Okay... let's, uh, ship it after the tests pass.",
                Clean = "Okay let's ship it after the tests pass." },
        ],
    };

    /// <summary>Rough English average for LLM tokenizers.</summary>
    public int EstimatedTokensSaved => (int)Math.Round(WordsRemoved * 1.3);

    public static HistoryStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<HistoryStore>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"History could not be read: {ex.Message}");
        }
        return new HistoryStore();
    }

    public RecentDictation Add(string raw, string clean)
    {
        var item = new RecentDictation
        {
            Time = DateTime.Now,
            Raw = raw,
            Clean = clean,
            WordsRemoved = Math.Max(0, TextCleaner.CountWords(raw) - TextCleaner.CountWords(clean)),
        };
        Recent.Insert(0, item);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        Dictations++;
        WordsRemoved += item.WordsRemoved;
        Save();
        return item;
    }

    public void ClearRecent()
    {
        Recent.Clear();
        Save();
    }

    void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            Log.Write($"History could not be saved: {ex.Message}");
        }
    }
}

public readonly record struct DiffPart(string Text, bool Removed);

/// <summary>Word-level diff that marks which parts of the raw transcript the cleaner removed.</summary>
public static class TextDiff
{
    // A word (letters/digits, with inner apostrophes or hyphens) or a run of punctuation, plus trailing spaces.
    static readonly Regex Token = new(@"[\p{L}\p{N}][\p{L}\p{N}'’-]*\s*|[^\p{L}\p{N}\s]+\s*|\s+");

    public static List<DiffPart> Compare(string raw, string clean)
    {
        var tokens = Token.Matches(raw).Select(m => m.Value).ToList();
        var rawKeys = tokens.Select(Key).ToList();
        var cleanKeys = Token.Matches(clean).Select(m => Key(m.Value)).Where(k => k.Length > 0).ToList();

        // Longest common subsequence over normalized words.
        int n = rawKeys.Count, m = cleanKeys.Count;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = rawKeys[i].Length > 0 && rawKeys[i] == cleanKeys[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var kept = new bool[n];
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (rawKeys[i].Length > 0 && rawKeys[i] == cleanKeys[j]) { kept[i] = true; i++; j++; }
            else if (lcs[i + 1, j] >= lcs[i, j + 1]) i++;
            else j++;
        }

        var parts = new List<DiffPart>();
        for (int i = 0; i < n; i++)
        {
            // Whitespace-only tokens never count as removed on their own.
            bool removed = !kept[i] && rawKeys[i].Length > 0;
            if (parts.Count > 0 && parts[^1].Removed == removed)
                parts[^1] = new DiffPart(parts[^1].Text + tokens[i], removed);
            else
                parts.Add(new DiffPart(tokens[i], removed));
        }
        return parts;
    }

    /// <summary>Words compare case-insensitively; punctuation compares exactly.</summary>
    static string Key(string token)
    {
        string t = token.Trim();
        return t.Length > 0 && char.IsLetterOrDigit(t[0]) ? t.ToLowerInvariant() : t;
    }
}
