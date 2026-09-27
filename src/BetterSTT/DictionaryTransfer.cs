using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BetterSTT;

/// <summary>
/// Exports the dictionary (vocabulary, word fixes, snippets) to a JSON file and imports it back, merging
/// with what's already there. Imports also accept a plain list of fixes, one per line: "heard,type" or
/// "heard => type".
/// </summary>
public static class DictionaryTransfer
{
    public sealed record Result(int Words, int Fixes, int Snippets)
    {
        public override string ToString()
        {
            var parts = new List<string>();
            if (Words > 0) parts.Add(Words == 1 ? "1 word" : $"{Words} words");
            if (Fixes > 0) parts.Add(Fixes == 1 ? "1 word fix" : $"{Fixes} word fixes");
            if (Snippets > 0) parts.Add(Snippets == 1 ? "1 snippet" : $"{Snippets} snippets");
            return parts.Count == 0 ? "nothing new" : string.Join(", ", parts);
        }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Export(AppSettings s) => new JsonObject
    {
        ["app"] = "BetterSTT",
        ["vocabulary"] = s.Vocabulary,
        ["replacements"] = new JsonArray(s.Replacements.Select(r => (JsonNode)new JsonObject { ["from"] = r.From, ["to"] = r.To }).ToArray()),
        ["snippets"] = new JsonArray(s.Snippets.Select(x => (JsonNode)new JsonObject { ["trigger"] = x.Trigger, ["text"] = x.Text }).ToArray()),
    }.ToJsonString(Json);

    /// <summary>Merges an exported file (or a list of fixes) into the settings. Existing entries are updated, not duplicated.</summary>
    public static Result Import(string text, AppSettings s)
    {
        text = text.Trim().TrimStart('﻿');
        return text.StartsWith('{') ? ImportJson(text, s) : ImportList(text, s);
    }

    static Result ImportJson(string text, AppSettings s)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("The file isn't a BetterSTT dictionary.");
        int words = 0, fixes = 0, snippets = 0;

        if (root["vocabulary"]?.GetValue<string>() is { } vocab)
        {
            var existing = Split(s.Vocabulary);
            foreach (var word in Split(vocab))
            {
                if (existing.Contains(word, StringComparer.OrdinalIgnoreCase)) continue;
                existing.Add(word);
                words++;
            }
            s.Vocabulary = string.Join(", ", existing);
        }
        foreach (var node in root["replacements"]?.AsArray() ?? [])
            if (AddFix(s, node?["from"]?.GetValue<string>(), node?["to"]?.GetValue<string>())) fixes++;
        foreach (var node in root["snippets"]?.AsArray() ?? [])
        {
            string trigger = node?["trigger"]?.GetValue<string>()?.Trim() ?? "", body = node?["text"]?.GetValue<string>()?.Trim() ?? "";
            if (trigger.Length == 0 || body.Length == 0) continue;
            var existing = s.Snippets.FirstOrDefault(x => string.Equals(x.Trigger.Trim(), trigger, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Text = body;
            else s.Snippets.Add(new Snippet { Trigger = trigger, Text = body });
            snippets++;
        }
        return new Result(words, fixes, snippets);
    }

    static Result ImportList(string text, AppSettings s)
    {
        int fixes = 0;
        foreach (string line in text.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^(?<from>.+?)\s*(?:=>|->|→|,|\t)\s*(?<to>.+)$");
            if (!m.Success) continue;
            string from = m.Groups["from"].Value.Trim('"', ' '), to = m.Groups["to"].Value.Trim('"', ' ');
            if (from.Equals("heard", StringComparison.OrdinalIgnoreCase) || from.Equals("from", StringComparison.OrdinalIgnoreCase)) continue; // header row
            if (AddFix(s, from, to)) fixes++;
        }
        if (fixes == 0) throw new InvalidDataException("No word fixes were found. Use one per line, like: git hub, GitHub");
        return new Result(0, fixes, 0);
    }

    static bool AddFix(AppSettings s, string? from, string? to)
    {
        from = from?.Trim() ?? "";
        to = to?.Trim() ?? "";
        if (from.Length == 0 || to.Length == 0 || from == to) return false;
        var existing = s.Replacements.FirstOrDefault(r => string.Equals(r.From.Trim(), from, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.To = to;
        else s.Replacements.Add(new Replacement { From = from, To = to });
        return true;
    }

    static List<string> Split(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
