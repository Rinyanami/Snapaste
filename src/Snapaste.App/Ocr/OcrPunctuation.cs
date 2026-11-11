using System.Text;
using System.Text.RegularExpressions;

namespace Snapaste.Ocr;

public enum OcrPunctuationMode
{
    Smart,
    Original,
    Chinese,
    English
}

public static partial class OcrPunctuation
{
    private enum Script { Unknown, Han, Latin, Other }
    private readonly record struct Context(Script Nearest, bool HasHan, bool HasOther, int LatinLetters, int LatinWords);
    private const int ContextLimit = 96;

    public static string Format(string text, OcrPunctuationMode mode)
    {
        if (text.Length == 0 || mode is not (OcrPunctuationMode.Smart or OcrPunctuationMode.Chinese or OcrPunctuationMode.English))
            return text;

        bool[] protectedText;
        try { protectedText = ProtectTechnicalText(text); }
        catch (RegexMatchTimeoutException) { return text; }

        char[]? result = null;
        for (int i = 0; i < text.Length; i++)
        {
            if (protectedText[i] || !IsPunctuation(text[i])) continue;

            var script = mode switch
            {
                OcrPunctuationMode.Chinese => Script.Han,
                OcrPunctuationMode.English => Script.Latin,
                _ => GetLocalScript(text, protectedText, i)
            };
            if (script is not (Script.Han or Script.Latin)) continue;

            char replacement = ChangeWidth(text[i], script == Script.Han);
            if (replacement != text[i]) (result ??= text.ToCharArray())[i] = replacement;
        }
        FormatParentheses(text, protectedText, mode, ref result);
        CorrectCurlyQuotes(text, protectedText, ref result);
        return result == null ? text : new string(result);
    }

    private static void CorrectCurlyQuotes(string text, bool[] protectedText, ref char[]? result)
    {
        List<int>? quotes = null;
        int lineStart = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] is '\r' or '\n' or '\u2028' or '\u2029')
            {
                if (quotes is { Count: > 0 }) { CorrectQuoteLine(text, protectedText, lineStart, quotes, ref result); quotes.Clear(); }
                lineStart = i + 1;
            }
            else if (!protectedText[i] && IsCurlyQuote(text[i]))
                (quotes ??= new List<int>()).Add(i);
        }
    }

    private static void CorrectQuoteLine(string text, bool[] protectedText, int lineStart, List<int> quotes, ref char[]? result)
    {
        if (quotes.Count % 2 != 0 || QuotesAreBalanced(text, quotes)) return;

        // Equal directions are a common OCR error. Reversed pairs and
        // possibly nested or unfinished quotations are ambiguous.
        for (int i = 0; i < quotes.Count; i += 2)
        {
            int start = quotes[i], end = quotes[i + 1];
            if (text[start] == '”' && text[end] == '“') return;
            if (FollowsDigit(text, lineStart, start) || FollowsDigit(text, lineStart, end)) return;
            if (!HasQuotedText(text, protectedText, start, end)) return;
            if (i > 0 && !HasClauseSeparator(text.AsSpan(quotes[i - 1] + 1, start - quotes[i - 1] - 1))) return;
        }

        for (int i = 0; i < quotes.Count; i += 2)
        {
            int start = quotes[i], end = quotes[i + 1];
            if (text[start] == '“' && text[end] == '”') continue;
            result ??= text.ToCharArray();
            result[start] = '“'; result[end] = '”';
        }
    }

    private static bool IsCurlyQuote(char c) => c is '“' or '”';

    private static bool QuotesAreBalanced(string text, List<int> quotes)
    {
        int depth = 0;
        foreach (int index in quotes)
        {
            if (text[index] == '“') depth++;
            else if (--depth < 0) return false;
        }
        return depth == 0;
    }

    private static bool FollowsDigit(string text, int lineStart, int quote)
    {
        int previous = quote - 1;
        while (previous >= lineStart && char.IsWhiteSpace(text[previous])) previous--;
        if (previous > lineStart && char.IsSurrogatePair(text[previous - 1], text[previous])) previous--;
        return previous >= lineStart && char.IsDigit(text, previous);
    }

    private static bool HasQuotedText(string text, bool[] protectedText, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            if (protectedText[i]) continue;
            if (char.IsLetter(text, i)) return true;
        }
        return false;
    }

    private static bool HasClauseSeparator(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
            if (IsPunctuation(c) || c == '、') return true;
        return false;
    }

    private static void FormatParentheses(string text, bool[] protectedText, OcrPunctuationMode mode, ref char[]? result)
    {
        Stack<int>? open = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\r' or '\n' or '\u2028' or '\u2029') { open?.Clear(); continue; }
            if (protectedText[i]) continue;
            if (c is '(' or '（') { (open ??= new Stack<int>()).Push(i); continue; }
            if (c is not (')' or '）') || open?.TryPop(out int start) != true) continue;
            if (start > 0 && text[start - 1] == ']') continue; // Markdown link destination.
            if (LooksLikeFormula(text.AsSpan(start + 1, i - start - 1))) continue;

            var script = mode switch
            {
                OcrPunctuationMode.Chinese => Script.Han,
                OcrPunctuationMode.English => Script.Latin,
                _ => GetParenthesisScript(text, protectedText, start, i)
            };
            if (script is not (Script.Han or Script.Latin)) continue;
            char left = script == Script.Han ? '（' : '(', right = script == Script.Han ? '）' : ')';
            if (left == text[start] && right == c) continue;
            result ??= text.ToCharArray();
            result[start] = left; result[i] = right;
        }
    }

    private static Script GetParenthesisScript(string text, bool[] protectedText, int start, int end)
    {
        var before = ReadContext(text, protectedText, start, -1);
        var after = ReadContext(text, protectedText, end, 1);
        var inside = ReadContext(text, protectedText, start, 1, end);
        if (before.Nearest == Script.Other || inside.HasOther) return Script.Unknown;
        if (before.Nearest == Script.Han && !before.HasOther) return Script.Han;
        if (before.Nearest == Script.Unknown && after.Nearest == Script.Han && !after.HasOther) return Script.Han;
        if (before.Nearest == Script.Latin && !before.HasHan && before.LatinLetters >= 2) return Script.Latin;
        if (inside.HasHan && inside.LatinLetters == 0) return Script.Han;
        return !inside.HasHan && inside.LatinLetters >= 2 ? Script.Latin : Script.Unknown;
    }

    private static bool LooksLikeFormula(ReadOnlySpan<char> content)
    {
        if (content.Length > ContextLimit) return false;
        bool operand = false, operation = false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (char.IsDigit(c)) operand = true;
            else if (char.IsAsciiLetter(c))
            {
                if (i > 0 && char.IsAsciiLetter(content[i - 1])) return false;
                operand = true;
            }
            else if (c is '+' or '-' or '−' or '×' or '÷' or '*' or '/' or '^' or '=' or '<' or '>' or '≤' or '≥' or ',' or '，' or ':' or '：') operation = true;
            else if (!char.IsWhiteSpace(c) && c is not ('.' or '．' or '(' or ')' or '（' or '）' or '%'))
                return false;
        }
        return operand && operation;
    }

    private static bool IsPunctuation(char c) => c is ',' or '，' or ':' or '：' or ';' or '；' or '?' or '？' or '!' or '！' or '.' or '。';

    private static char ChangeWidth(char c, bool chinese) => c switch
    {
        ',' or '，' => chinese ? '，' : ',',
        ':' or '：' => chinese ? '：' : ':',
        ';' or '；' => chinese ? '；' : ';',
        '?' or '？' => chinese ? '？' : '?',
        '!' or '！' => chinese ? '！' : '!',
        '.' or '。' => chinese ? '。' : '.',
        _ => c
    };

    private static Script GetLocalScript(string text, bool[] protectedText, int index)
    {
        if ((text[index] is ';' or '；') && SeparatesHanFields(text, protectedText, index)) return Script.Han;

        var left = ReadContext(text, protectedText, index, -1);
        var right = ReadContext(text, protectedText, index, 1);

        // The previous clause usually owns its ending punctuation.
        if (left.Nearest == Script.Han) return left.HasOther ? Script.Unknown : Script.Han;
        if (left.Nearest == Script.Other) return Script.Unknown;
        if (left.Nearest == Script.Unknown)
        {
            if (right.Nearest == Script.Han && !right.HasOther) return Script.Han;
            return right.Nearest == Script.Latin && right.LatinLetters >= 2 ? Script.Latin : Script.Unknown;
        }
        if (!left.HasHan)
            return left.LatinLetters >= 2 || right.Nearest == Script.Latin && right.LatinLetters >= 2 ? Script.Latin : Script.Unknown;

        // A complete English phrase after a Chinese label keeps its own
        // punctuation. One Latin word in a mixed clause can also be a name.
        if (left.LatinWords >= 2) return Script.Latin;
        if (text[index] is ',' or '，' or ';' or '；' && right.Nearest == Script.Latin && !right.HasHan)
            return Script.Latin;
        return !left.HasOther && right.HasHan && !right.HasOther ? Script.Han : Script.Unknown;
    }

    private static bool SeparatesHanFields(string text, bool[] protectedText, int index)
    {
        // The separator belongs to the field labels even if a value is Latin,
        // such as 编号：AB-0123;版本：v0.6.0. Require labels on both sides.
        if (!ReadHanFieldLabel(text, protectedText, index, 1, out int rightColon)
            || (uint)rightColon >= (uint)text.Length || protectedText[rightColon]
            || text[rightColon] is not (':' or '：')) return false;

        for (int i = index - 1; i >= 0 && index - i <= ContextLimit; i--)
        {
            char c = text[i];
            if (c is '\r' or '\n' or '\u2028' or '\u2029') break;
            if (protectedText[i]) continue;
            if (c is '=' or '＝' or '{' or '}' or '[' or ']' or '<' or '>') return false;
            if (!IsPunctuation(c)) continue;
            if (c is not (':' or '：') || !ReadHanFieldLabel(text, protectedText, i, -1, out int beforeLabel)) return false;
            var value = text.AsSpan(i + 1, index - i - 1).Trim();
            int space = value.IndexOfAny(' ', '\t', '\u3000');
            if (space >= 0) value = value[..space];
            if (value is "using" or "import" or "return" or "throw" or "break" or "continue" or "goto" or "yield") return false;
            if (beforeLabel < 0) return true;
            char boundary = text[beforeLabel];
            return boundary is '\r' or '\n' or '\u2028' or '\u2029'
                || !protectedText[beforeLabel] && (IsPunctuation(boundary) || boundary is '、' or '】');
        }
        return false;
    }

    private static bool ReadHanFieldLabel(string text, bool[] protectedText, int from, int direction, out int after)
    {
        after = from + direction;
        while ((uint)after < (uint)text.Length && Math.Abs(after - from) <= ContextLimit && IsFieldSpace(text[after])) after += direction;
        int letters = 0;
        while ((uint)after < (uint)text.Length && !protectedText[after] && Classify(text[after]) == Script.Han)
        {
            if (++letters > 12) return false;
            after += direction;
        }
        while ((uint)after < (uint)text.Length && Math.Abs(after - from) <= ContextLimit && IsFieldSpace(text[after])) after += direction;
        return letters > 0;
    }

    private static bool IsFieldSpace(char c) => c is ' ' or '\t' or '\u00a0' or '\u3000';

    private static Context ReadContext(string text, bool[] protectedText, int index, int direction, int boundary = -1)
    {
        Script nearest = Script.Unknown, previous = Script.Unknown;
        bool han = false, other = false, adjacent = true;
        int latinLetters = 0, latinWords = 0;
        for (int i = index + direction; (uint)i < (uint)text.Length && i != boundary && Math.Abs(i - index) <= ContextLimit; i += direction)
        {
            char c = text[i];
            if (c is '\r' or '\n' or '\u2028' or '\u2029') break;
            if (protectedText[i])
            {
                previous = Script.Unknown;
                adjacent = false;
                continue;
            }
            if (IsPunctuation(c))
            {
                if (adjacent) continue; // Treat a contiguous ?! run as one ending.
                break;
            }
            adjacent = false;
            int value = c;
            if (direction > 0 && char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                value = char.ConvertToUtf32(c, text[++i]);
            else if (direction < 0 && char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(text[i - 1]))
                value = char.ConvertToUtf32(text[--i], c);

            var script = Classify(value);
            if (nearest == Script.Unknown) nearest = script;
            if (script == Script.Han) han = true;
            else if (script == Script.Other) other = true;
            else if (script == Script.Latin)
            {
                latinLetters++;
                if (previous != Script.Latin) latinWords++;
            }
            previous = script;
        }
        return new Context(nearest, han, other, latinLetters, latinWords);
    }

    private static Script Classify(int value)
    {
        if (value is 0x3005 or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF)
            return Script.Han;
        if (value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 0x00C0 and <= 0x024F or >= 0x1E00 and <= 0x1EFF or >= 0xFF21 and <= 0xFF3A or >= 0xFF41 and <= 0xFF5A)
            return Rune.TryCreate(value, out var latin) && Rune.IsLetter(latin) ? Script.Latin : Script.Unknown;
        return Rune.TryCreate(value, out var rune) && Rune.IsLetter(rune) ? Script.Other : Script.Unknown;
    }

    private static bool[] ProtectTechnicalText(string text)
    {
        var protectedText = new bool[text.Length];
        foreach (Match match in TechnicalSpans().Matches(text))
            protectedText.AsSpan(match.Index, match.Length).Fill(true);

        // Function arguments may contain nested calls, parentheses in quotes,
        // and literal commas. Only accept a balanced, single-line call.
        int protectedThrough = 0;
        foreach (Match match in CallStarts().Matches(text))
        {
            if (match.Index < protectedThrough || protectedText[match.Index + match.Length - 1]) continue;
            int end = FindCallEnd(text, match.Index + match.Length);
            if (end < 0) continue;
            protectedText.AsSpan(match.Index, end - match.Index).Fill(true);
            protectedThrough = end;
        }
        return protectedText;
    }

    private static int FindCallEnd(string text, int start)
    {
        const int maxCallLength = 2048;
        int depth = 1, limit = Math.Min(text.Length, start + maxCallLength);
        char quote = '\0';
        for (int i = start; i < limit; i++)
        {
            char c = text[i];
            if (c is '\r' or '\n') break;
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote || IsCurlyQuote(quote) && IsCurlyQuote(c)) quote = '\0';
            }
            else if (c is '"' or '\'' || IsCurlyQuote(c)) quote = c;
            else if (c is '(' or '（') depth++;
            else if ((c is ')' or '）') && --depth == 0)
            {
                return i + 1 < text.Length && text[i + 1] is ';' or '；' ? i + 2 : i + 1;
            }
        }
        return -1;
    }

    [GeneratedRegex("""
        # Fenced and inline code, then common standalone code and math lines.
        (?m:^[ \t]*(?<fence>[~]{3,}|\x60{3,})[^\r\n]*(?:\r\n?|\n)[\s\S]*?(?:^[ \t]*\k<fence>[ \t]*(?=\r?$)|\z))
        | \x60+[^\x60\r\n]+\x60+
        | (?m:^[ \t]*(?:
              (?:using|namespace|var|let|const|function|class|public|private|return|if|for|foreach|while|switch)\b(?=[^\r\n]*[;={}()<>])
              | [A-Za-z_][A-Za-z0-9_.]*[ \t]*(?:[<>!+*/-]?=|::)
              | // | /[*] | [#](?:!|include\b|define\b)
              | [\[{][ \t]*(?=["{\[0-9]) | "(?:[^"\\\r\n]|\\.)+"[ \t]*[:：]
          )[^\r\n]*)
        # URLs, paths, mailboxes, and bare domains. Adjacent ASCII marks on a URL
        # can be valid URI characters, so leave the whole token unchanged.
        | (?:[A-Za-z][A-Za-z0-9+.-]{1,31}[:：]//|www[.．]|[A-Za-z][:：][\\/]|\\\\|(?<![A-Za-z0-9_)\]）])(?:[~.]{1,2})?/+(?=[A-Za-z0-9_.-]))
          [^\s<>"'，。；：？！、）】》」』]+
        | (?<![A-Za-z0-9.!#$%&'*+/=?^_\x60{|}~-])[A-Za-z0-9.!#$%&'*+/=?^_\x60{|}~-]+[@＠][\p{L}0-9-]+(?:[.．][\p{L}0-9-]+)+
        | (?<![A-Za-z0-9_.-])(?:[A-Za-z0-9][A-Za-z0-9-]{0,62}[.．])+[A-Za-z]{2,63}(?::[0-9]+)?(?:[/#?][^\s<>"'，。；：？！、）】》」』]*)?
        | (?<![A-Za-z0-9_])(?:[CF][#＃]|C[+＋]{2}|[.．](?:NET|gitignore|env|bashrc|npmrc|config|git))(?![A-Za-z0-9_])
        | [*?][.．][A-Za-z0-9]{1,10}
        # Versions, grouped numbers, dates, times, numbering, and operators.
        | [vV]?[0-9０-９]+(?:[.,，．:：/\\\-][0-9０-９]+)+
        | (?m:^[ \t]*(?:[0-9０-９]+|[A-Za-z])[.．][ \t]+)
        | (?<![A-Za-z])(?:[A-Za-z][.．]){2,}
        | (?<![A-Za-z])(?:Mr|Mrs|Ms|Dr|Prof|Sr|Jr|St|vs|etc)[.．](?![A-Za-z])
        | (?:[.．。]{2,}|[!！][=＝~]|[:：]{2}|[?？][?？.]|(?:[A-Za-z_][A-Za-z0-9_.]*[ \t]*)[?？](?:[ \t]*[A-Za-z0-9_]+)[ \t]*[:：][ \t]*[A-Za-z0-9_]+)
        | (?<![A-Za-z0-9_-])--[A-Za-z0-9_][A-Za-z0-9_-]*(?:=[^\s，。；：？！、]+)?
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant, 500)]
    private static partial Regex TechnicalSpans();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(?:(?:(?:if|for|foreach|while|switch)[ \t]*|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)[(（]|[A-Za-z_][A-Za-z0-9_]*\()", RegexOptions.CultureInvariant, 500)]
    private static partial Regex CallStarts();
}
