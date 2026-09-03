namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlFragmentCanonicalizer
{
    public static string Canonicalize(string sqlFragment)
    {
        ArgumentNullException.ThrowIfNull(sqlFragment);

        var tokens = Tokenize(sqlFragment);
        return string.Join(' ', tokens);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (StartsWith(text, index, "--"))
            {
                index = SkipLineComment(text, index + 2);
                continue;
            }

            if (StartsWith(text, index, "/*"))
            {
                index = SkipBlockComment(text, index + 2);
                continue;
            }

            tokens.Add(text[index] switch
            {
                '\'' => ReadDelimited(text, ref index, '\'', "single-quoted string", doubledEscape: true),
                '"' => ReadDelimited(text, ref index, '"', "double-quoted identifier", doubledEscape: true),
                '[' => ReadBracketIdentifier(text, ref index),
                _ => ReadExecutableToken(text, ref index)
            });
        }

        return tokens;
    }

    private static string ReadDelimited(
        string text,
        ref int index,
        char delimiter,
        string description,
        bool doubledEscape)
    {
        var start = index++;
        while (index < text.Length)
        {
            if (text[index] != delimiter)
            {
                index++;
                continue;
            }

            if (doubledEscape && index + 1 < text.Length && text[index + 1] == delimiter)
            {
                index += 2;
                continue;
            }

            index++;
            return text[start..index];
        }

        throw new InvalidOperationException($"Unterminated {description} in SQL fragment.");
    }

    private static string ReadBracketIdentifier(string text, ref int index)
    {
        var start = index++;
        while (index < text.Length)
        {
            if (text[index] != ']')
            {
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == ']')
            {
                index += 2;
                continue;
            }

            index++;
            return text[start..index];
        }

        throw new InvalidOperationException("Unterminated bracket identifier in SQL fragment.");
    }

    private static string ReadExecutableToken(string text, ref int index)
    {
        var start = index;
        if (IsPunctuation(text[index]))
        {
            index++;
            return text[start..index];
        }

        if (IsOperatorStart(text[index]))
        {
            index++;
            if (index < text.Length && IsOperatorPair(text[start], text[index]))
            {
                index++;
            }

            return text[start..index];
        }

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]) ||
                IsPunctuation(text[index]) ||
                IsOperatorStart(text[index]) ||
                StartsWith(text, index, "--") ||
                StartsWith(text, index, "/*") ||
                text[index] is '\'' or '"' or '[')
            {
                break;
            }

            index++;
        }

        if (index == start)
        {
            index++;
        }

        return text[start..index];
    }

    private static int SkipLineComment(string text, int index)
    {
        while (index < text.Length && text[index] is not '\r' and not '\n')
        {
            index++;
        }

        return index;
    }

    private static int SkipBlockComment(string text, int index)
    {
        var depth = 1;
        while (index < text.Length)
        {
            if (StartsWith(text, index, "/*"))
            {
                depth++;
                index += 2;
                continue;
            }

            if (StartsWith(text, index, "*/"))
            {
                depth--;
                index += 2;
                if (depth == 0)
                {
                    return index;
                }

                continue;
            }

            index++;
        }

        throw new InvalidOperationException("Unterminated block comment in SQL fragment.");
    }

    private static bool StartsWith(string text, int index, string value) =>
        index + value.Length <= text.Length &&
        text.AsSpan(index, value.Length).SequenceEqual(value);

    private static bool IsPunctuation(char value) => value is '(' or ')' or ',' or '.' or ';';

    private static bool IsOperatorStart(char value) => value is '=' or '>' or '<' or '!' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '~';

    private static bool IsOperatorPair(char first, char second) =>
        (first, second) is
            ('>', '=') or
            ('<', '=') or
            ('<', '>') or
            ('!', '=') or
            ('!', '<') or
            ('!', '>');
}
