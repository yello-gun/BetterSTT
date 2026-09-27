using System.Text.RegularExpressions;

namespace BetterSTT;

/// <summary>
/// Turns spoken code into identifiers and symbols: "camel case user name" → userName, "file dot txt" →
/// file.txt. Only what's said explicitly is converted; nothing is guessed or added.
/// </summary>
public static class SpokenCode
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // A case command takes the words after it, up to punctuation, a symbol word or five words.
    const string Breaks = @"equals|plus|minus|times|dot|and|or|is|to|in|of|with|for|then|open|close|colon|semicolon|comma|underscore|slash|dash|arrow";
    static readonly Regex CaseCommand = new(
        $@"\b(?<k>camel|pascal|snake|kebab|constant|screaming\s+snake|all\s+caps)\s+case\s+(?<w>(?:(?!(?:{Breaks})\b)[\p{{L}}\p{{N}}]+(?:\s+|$)){{1,5}})",
        Opts);
    static readonly Regex GluedCase = new(
        @"\b(?<k>camel|pascal|snake|kebab|constant)[_-]?case[_-]?(?<w>[\p{L}\p{N}][\p{L}\p{N}_-]*)", Opts);
    static readonly Regex AllCaps = new(
        $@"\ball\s+caps\s+(?<w>(?:(?!(?:{Breaks})\b)[\p{{L}}\p{{N}}]+(?:\s+|$)){{1,5}})", Opts);

    // Spoken symbol → text. Joining symbols stick to both neighbours ("file dot txt" → file.txt).
    static readonly (string Words, string Symbol, bool Join)[] Symbols =
    [
        ("double equals", " == ", false), ("not equals", " != ", false), ("fat arrow", " => ", false), ("arrow", " -> ", false),
        ("equals sign", " = ", false), ("equals", " = ", false), ("dot", ".", true), ("underscore", "_", true), ("dash", "-", true), ("hyphen", "-", true),
        ("backslash", "\\", true), ("forward slash", "/", true), ("slash", "/", true), ("at sign", "@", true),
        ("hash sign", "#", false), ("dollar sign", "$", true), ("backtick", "`", false), ("pipe", " | ", false),
        ("open paren", "(", false), ("close paren", ")", false), ("open parenthesis", "(", false), ("close parenthesis", ")", false),
        ("open bracket", "[", false), ("close bracket", "]", false), ("open brace", "{", false), ("close brace", "}", false),
        ("open curly brace", "{", false), ("close curly brace", "}", false), ("semicolon", ";", false), ("colon", ":", false),
    ];

    public static string Convert(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string s = text.TrimEnd();
        if (s.EndsWith('.') && !s.EndsWith("..")) s = s[..^1];
        string baseline = s;

        // Speech recognition sometimes glues the command to the words: "camelCaseUserName", "snake_case_user_name".
        s = GluedCase.Replace(s, m => Identifier(m.Groups["k"].Value,
            Regex.Split(Regex.Replace(m.Groups["w"].Value, @"(?<=\p{Ll})(?=\p{Lu})", " "), @"[\s_-]+")
                .Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList()));
        s = CaseCommand.Replace(s, m => Identifier(m.Groups["k"].Value, Words(m.Groups["w"].Value)) + (m.Value.EndsWith(' ') ? " " : ""));
        s = AllCaps.Replace(s, m => string.Join("_", Words(m.Groups["w"].Value)).ToUpperInvariant() + (m.Value.EndsWith(' ') ? " " : ""));

        foreach (var (words, symbol, join) in Symbols.OrderByDescending(x => x.Words.Length))
        {
            string pattern = string.Join(@"\s+", words.Split(' ').Select(Regex.Escape));
            var rx = new Regex(join ? $@"\s*,?\s*\b{pattern}\b\s*,?\s*" : $@"\b{pattern}\b", Opts);
            s = rx.Replace(s, join ? symbol.Trim() : "\u0007" + symbol + "\u0007");
        }
        // Brackets stick to what they enclose; other symbols keep one space either side.
        s = Regex.Replace(s, "\\s*\u0007([(\\[{])\u0007\\s*", "$1");
        s = Regex.Replace(s, "\\s*\u0007([)\\]}])\u0007", "$1");
        s = Regex.Replace(s, "\\s*\u0007([;:])\u0007", "$1");
        s = s.Replace("\u0007", "");
        s = Regex.Replace(s, @" {2,}", " ").Trim();

        return s == baseline ? text : s; // unchanged when nothing code-like was said
    }

    static List<string> Words(string words) =>
        words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();

    static string Identifier(string kind, List<string> words)
    {
        static string Title(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];
        return Regex.Replace(kind.ToLowerInvariant(), @"\s+", " ") switch
        {
            "camel" => words[0] + string.Concat(words.Skip(1).Select(Title)),
            "pascal" => string.Concat(words.Select(Title)),
            "snake" => string.Join("_", words),
            "kebab" => string.Join("-", words),
            _ => string.Join("_", words).ToUpperInvariant(), // constant, screaming snake, all caps
        };
    }
}
