using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterSTT;

/// <summary>
/// Rewrites spoken math as symbols or LaTeX: "x squared plus one over two" → "x² + 1/2" or "x^2 + \frac{1}{2}".
/// Rule-based, like the cleanup rules: number words, operators, powers, roots, Greek letters and brackets.
/// </summary>
public static class SpokenMath
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Markers used while rewriting: a binary operator is wrapped in Op…End, a token that sticks to the word
    // before it follows Left, and one that sticks to the word after it is followed by Right.
    const char Op = '\u0003', End = '\u0004', Left = '\u0005', Right = '\u0006';

    // ---- number words ----

    static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
    };

    static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6,
        ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10,
    };

    static readonly Regex NumberRun;

    static SpokenMath()
    {
        string word = string.Join("|", Units.Keys.Concat(Tens.Keys).Append("hundred").Append("thousand")
            .OrderByDescending(w => w.Length));
        NumberRun = new Regex($@"(?<![\p{{L}}])(?:{word})(?:[\s-]+(?:{word}))*(?![\p{{L}}])", Opts);
    }

    /// <summary>"twenty five" → "25", "one hundred and three" stays simple: runs like "one four" become "1 4".</summary>
    static string NumbersToDigits(string text) => NumberRun.Replace(text, m =>
    {
        var words = Regex.Split(m.Value.Trim(), @"[\s-]+");
        var numbers = new List<long>();
        long total = 0, current = 0;
        bool any = false;
        string? previous = null;

        void Flush()
        {
            if (any) numbers.Add(total + current);
            total = current = 0;
            any = false;
        }

        foreach (string w in words)
        {
            if (Units.TryGetValue(w, out int u))
            {
                // "one four" is two numbers; "twenty five" is one.
                bool joins = previous != null && (Tens.ContainsKey(previous) && u < 10 || previous is "hundred" or "thousand");
                if (any && !joins) Flush();
                current += u;
                any = true;
            }
            else if (Tens.TryGetValue(w, out int t))
            {
                bool joins = previous is "hundred" or "thousand";
                if (any && !joins) Flush();
                current += t;
                any = true;
            }
            else if (w.Equals("hundred", StringComparison.OrdinalIgnoreCase))
            {
                current = (any ? Math.Max(1, current) : 1) * 100;
                any = true;
            }
            else
            {
                total += (any ? Math.Max(1, current) : 1) * 1000;
                current = 0;
                any = true;
            }
            previous = w.ToLowerInvariant();
        }
        Flush();
        return string.Join(" ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
    });

    // ---- phrases ----

    /// <summary>Spoken phrase → (symbol form, LaTeX form, kind). Longest phrases are matched first.</summary>
    enum Kind { Binary, Postfix, Prefix, Word }

    static readonly (string[] Phrases, string Symbol, string Latex, Kind Kind)[] Rules =
    [
        (["plus or minus", "plus minus"], "±", @"\pm", Kind.Binary),
        (["is less than or equal to", "less than or equal to"], "≤", @"\leq", Kind.Binary),
        (["is greater than or equal to", "greater than or equal to"], "≥", @"\geq", Kind.Binary),
        (["is not equal to", "not equal to", "does not equal"], "≠", @"\neq", Kind.Binary),
        (["is approximately equal to", "approximately equal to", "is approximately", "approximately equals"], "≈", @"\approx", Kind.Binary),
        (["is equal to", "equals", "equal to"], "=", "=", Kind.Binary),
        (["is less than", "less than"], "<", "<", Kind.Binary),
        (["is greater than", "greater than"], ">", ">", Kind.Binary),
        (["plus"], "+", "+", Kind.Binary),
        (["minus"], "-", "-", Kind.Binary),
        (["multiplied by", "times"], "×", @"\times", Kind.Binary),
        (["divided by"], "÷", @"\div", Kind.Binary),
        (["over"], "/", "/", Kind.Binary),
        (["squared"], "²", "^2", Kind.Postfix),
        (["cubed"], "³", "^3", Kind.Postfix),
        (["factorial"], "!", "!", Kind.Postfix),
        (["degrees"], "°", @"^\circ", Kind.Postfix),
        (["percent"], "%", @"\%", Kind.Postfix),
        (["the square root of", "square root of", "square root"], "√", @"\sqrt", Kind.Prefix),
        (["the cube root of", "cube root of", "cube root"], "∛", @"\sqrt[3]", Kind.Prefix),
        (["open parenthesis", "open paren", "left parenthesis", "open bracket"], "(", "(", Kind.Prefix),
        (["close parenthesis", "close paren", "right parenthesis", "close bracket"], ")", ")", Kind.Postfix),
        (["pi"], "π", @"\pi", Kind.Word),
        (["infinity"], "∞", @"\infty", Kind.Word),
        (["alpha"], "α", @"\alpha", Kind.Word),
        (["beta"], "β", @"\beta", Kind.Word),
        (["gamma"], "γ", @"\gamma", Kind.Word),
        (["delta"], "δ", @"\delta", Kind.Word),
        (["epsilon"], "ε", @"\epsilon", Kind.Word),
        (["theta"], "θ", @"\theta", Kind.Word),
        (["lambda"], "λ", @"\lambda", Kind.Word),
        (["mu"], "μ", @"\mu", Kind.Word),
        (["sigma"], "σ", @"\sigma", Kind.Word),
        (["phi"], "φ", @"\phi", Kind.Word),
        (["omega"], "ω", @"\omega", Kind.Word),
    ];

    static readonly Regex Power = new(
        @"\s+to\s+the\s+(?:power\s+of\s+)?(?<e>\d+|[a-z]|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|nth)(?:st|nd|rd|th)?(?:\s+power)?(?![\p{L}\p{N}])", Opts);
    static readonly Regex Subscript = new(@"\s+sub\s+(?<s>\d+|[a-z])(?![\p{L}\p{N}])", Opts);
    static readonly Regex Decimal = new(@"(?<![\d.])(?<i>\d+)\s+point\s+(?<f>\d(?:\s+\d)*)(?![\d.])", Opts);

    const string Operand = @"(?:\([^()]*\)|\\[a-z]+(?:\{[^{}]*\})?|[\p{L}\p{N}.]+)(?:\^(?:\{[^{}]*\}|\\?[\p{L}\p{N}]+)|[²³⁰¹⁴-⁹ⁿⁱ]+)?";
    static readonly Regex Fraction = new($@"(?<a>{Operand}) / (?<b>{Operand})", Opts);
    static readonly Regex LatexRoot = new($@"(?<cmd>\\sqrt(?:\[3\])?){Right}\s*(?<x>{Operand})", Opts);

    const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";
    const string Subscripts = "₀₁₂₃₄₅₆₇₈₉";

    /// <summary>Converts spoken math in <paramref name="text"/>; unchanged when the format is Off.</summary>
    public static string Convert(string text, MathFormat format)
    {
        if (format == MathFormat.Off || string.IsNullOrWhiteSpace(text)) return text;
        bool latex = format == MathFormat.Latex;
        // Speech recognition ends with a period, which doesn't belong in a formula.
        string s = NumbersToDigits(text).TrimEnd();
        if (s.EndsWith('.') && !s.EndsWith("..")) s = s[..^1];
        string baseline = s;
        s = Decimal.Replace(s, m => m.Groups["i"].Value + "." + Regex.Replace(m.Groups["f"].Value, @"\s+", ""));

        // Powers and subscripts first, while "to the" and "sub" are still words.
        s = Power.Replace(s, m =>
        {
            string e = m.Groups["e"].Value.ToLowerInvariant();
            if (Ordinals.TryGetValue(e, out int n)) e = n.ToString(CultureInfo.InvariantCulture);
            if (e == "nth") e = "n";
            return Left + (latex ? (e.Length == 1 ? "^" + e : "^{" + e + "}") : Superscript(e));
        });
        s = Subscript.Replace(s, m =>
        {
            string v = m.Groups["s"].Value.ToLowerInvariant();
            return Left + (latex ? (v.Length == 1 ? "_" + v : "_{" + v + "}") : Sub(v));
        });

        // Longest phrase first, so "less than or equal to" is taken before "less than" and "equal to".
        foreach (var (phrase, form, kind) in PhraseList(latex))
        {
            string pattern = string.Join(@"\s+", phrase.Split(' ').Select(Regex.Escape));
            var rx = new Regex($@"(?<![\p{{L}}\p{{N}}\\])(?:{pattern})(?![\p{{L}}\p{{N}}])", Opts);
            s = rx.Replace(s, kind switch
            {
                Kind.Binary => $"{Op}{form}{End}",
                Kind.Postfix => Left + form,
                Kind.Prefix => form + Right,
                _ => latex ? form + " " : form,
            });
        }

        if (s == baseline) return text; // nothing mathematical was said

        // A minus at the start, after an operator or after "(" is a negative sign: "-5".
        s = Regex.Replace(s, $@"(^|\({Right}?|{End})\s*{Op}-{End}\s*", "$1\u0007");
        s = Regex.Replace(s, $@"\s*{Op}(.*?){End}\s*", " $1 ");
        s = s.Replace('\u0007', '-');
        s = Regex.Replace(s, $@"\s*{Left}", "");
        if (latex) s = LatexRoot.Replace(s, m => $"{m.Groups["cmd"].Value}{{{Unwrap(m.Groups["x"].Value)}}}");
        s = Regex.Replace(s, $@"{Right}\s*", "");
        s = Regex.Replace(s, @"\(\s+", "(");
        s = Regex.Replace(s, @"\s+\)", ")");

        s = latex
            ? Fraction.Replace(s, m => $@"\frac{{{Unwrap(m.Groups["a"].Value)}}}{{{Unwrap(m.Groups["b"].Value)}}}")
            : Fraction.Replace(s, "${a}/${b}");

        // "2 x" → "2x", "2 π" → "2π": a number times a single-letter variable.
        s = Regex.Replace(s, @"(?<=(?<![\p{L}])\d)\s+(?=[a-zα-ω](?![\p{L}\p{N}]))", "");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        // Speech recognition capitalizes the first word, which would turn the variable x into X.
        return Regex.Replace(s, @"^[XYZ](?![\p{L}])", m => m.Value.ToLowerInvariant());
    }

    static IEnumerable<(string Phrase, string Form, Kind Kind)> PhraseList(bool latex) => Rules
        .SelectMany(r => r.Phrases.Select(p => (p, latex ? r.Latex : r.Symbol, r.Kind)))
        .OrderByDescending(r => r.p.Length);

    static string Unwrap(string operand) =>
        operand.Length > 1 && operand[0] == '(' && operand[^1] == ')' && operand.IndexOf(')') == operand.Length - 1
            ? operand[1..^1]
            : operand;

    static string Superscript(string e)
    {
        if (e == "n") return "ⁿ";
        if (e == "i") return "ⁱ";
        if (!e.All(char.IsDigit)) return "^" + e;
        var sb = new StringBuilder();
        foreach (char c in e) sb.Append(Superscripts[c - '0']);
        return sb.ToString();
    }

    static string Sub(string v)
    {
        if (!v.All(char.IsDigit)) return "_" + v;
        var sb = new StringBuilder();
        foreach (char c in v) sb.Append(Subscripts[c - '0']);
        return sb.ToString();
    }
}
