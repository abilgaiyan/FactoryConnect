using System.Text;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlMigrationLexicalPolicy
{
    public static void Validate(string sql, SqlMigrationTransactionPolicy transactionPolicy)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var executable = ExtractExecutableText(sql);
        ValidateGoDirectives(executable);

        if (transactionPolicy == SqlMigrationTransactionPolicy.EngineOwned)
        {
            ValidateTransactionControl(executable);
        }
    }

    private static string ExtractExecutableText(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var state = LexicalState.Code;
        var blockCommentDepth = 0;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';

            switch (state)
            {
                case LexicalState.Code:
                    if (current == '\'' )
                    {
                        state = LexicalState.String;
                        result.Append(' ');
                    }
                    else if (current == '"')
                    {
                        state = LexicalState.QuotedIdentifier;
                        result.Append(' ');
                    }
                    else if (current == '[')
                    {
                        state = LexicalState.BracketIdentifier;
                        result.Append(' ');
                    }
                    else if (current == '-' && next == '-')
                    {
                        state = LexicalState.LineComment;
                        result.Append("  ");
                        index++;
                    }
                    else if (current == '/' && next == '*')
                    {
                        state = LexicalState.BlockComment;
                        blockCommentDepth = 1;
                        result.Append("  ");
                        index++;
                    }
                    else
                    {
                        result.Append(current);
                    }
                    break;

                case LexicalState.String:
                    if (current == '\'' && next == '\'')
                    {
                        result.Append("  ");
                        index++;
                    }
                    else if (current == '\'')
                    {
                        state = LexicalState.Code;
                        result.Append(' ');
                    }
                    else
                    {
                        PreserveLineBreak(result, current);
                    }
                    break;

                case LexicalState.QuotedIdentifier:
                    if (current == '"' && next == '"')
                    {
                        result.Append("  ");
                        index++;
                    }
                    else if (current == '"')
                    {
                        state = LexicalState.Code;
                        result.Append(' ');
                    }
                    else
                    {
                        PreserveLineBreak(result, current);
                    }
                    break;

                case LexicalState.BracketIdentifier:
                    if (current == ']' && next == ']')
                    {
                        result.Append("  ");
                        index++;
                    }
                    else if (current == ']')
                    {
                        state = LexicalState.Code;
                        result.Append(' ');
                    }
                    else
                    {
                        PreserveLineBreak(result, current);
                    }
                    break;

                case LexicalState.LineComment:
                    if (current == '\n')
                    {
                        state = LexicalState.Code;
                        result.Append('\n');
                    }
                    else
                    {
                        result.Append(' ');
                    }
                    break;

                case LexicalState.BlockComment:
                    if (current == '/' && next == '*')
                    {
                        blockCommentDepth++;
                        result.Append("  ");
                        index++;
                    }
                    else if (current == '*' && next == '/')
                    {
                        blockCommentDepth--;
                        result.Append("  ");
                        index++;
                        if (blockCommentDepth == 0)
                        {
                            state = LexicalState.Code;
                        }
                    }
                    else
                    {
                        PreserveLineBreak(result, current);
                    }
                    break;

                default:
                    throw new InvalidOperationException("Unknown SQL lexical state.");
            }
        }

        if (state is LexicalState.String or LexicalState.QuotedIdentifier or LexicalState.BracketIdentifier or LexicalState.BlockComment)
        {
            throw new InvalidOperationException($"Migration SQL ends inside an unterminated {Describe(state)}.");
        }

        return result.ToString();
    }

    private static void ValidateGoDirectives(string executable)
    {
        using var reader = new StringReader(executable);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 2 || !trimmed.StartsWith("GO", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Length == 2)
            {
                throw new InvalidOperationException("Executable GO batch directives are not supported in migration SQL.");
            }

            var suffix = trimmed[2..];
            if (suffix[0] == ';')
            {
                throw new InvalidOperationException("GO-like batch directives are not supported in migration SQL.");
            }

            if (!char.IsWhiteSpace(suffix[0]))
            {
                continue;
            }

            suffix = suffix.Trim();
            if (suffix.Length == 0 || IsPositiveIntegerWithOptionalSemicolon(suffix))
            {
                throw new InvalidOperationException("Executable GO batch directives are not supported in migration SQL.");
            }
        }
    }

    private static bool IsPositiveIntegerWithOptionalSemicolon(string value)
    {
        if (value.EndsWith(';'))
        {
            value = value[..^1].TrimEnd();
        }

        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character < '0' || character > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateTransactionControl(string executable)
    {
        var tokens = Tokenize(executable);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (EqualsToken(token, "COMMIT") || EqualsToken(token, "ROLLBACK"))
            {
                throw new InvalidOperationException("Engine-owned migrations must not contain transaction-control statements.");
            }

            if (EqualsToken(token, "BEGIN") &&
                index + 1 < tokens.Count &&
                (EqualsToken(tokens[index + 1], "TRAN") || EqualsToken(tokens[index + 1], "TRANSACTION")))
            {
                throw new InvalidOperationException("Engine-owned migrations must not contain transaction-control statements.");
            }

            if (EqualsToken(token, "SAVE") &&
                index + 1 < tokens.Count &&
                (EqualsToken(tokens[index + 1], "TRAN") || EqualsToken(tokens[index + 1], "TRANSACTION")))
            {
                throw new InvalidOperationException("Engine-owned migrations must not contain transaction-control statements.");
            }
        }
    }

    private static List<string> Tokenize(string executable)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var character in executable)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                current.Append(character);
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool EqualsToken(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static void PreserveLineBreak(StringBuilder result, char current) =>
        result.Append(current == '\n' ? '\n' : ' ');

    private static string Describe(LexicalState state) => state switch
    {
        LexicalState.String => "string literal",
        LexicalState.QuotedIdentifier => "quoted identifier",
        LexicalState.BracketIdentifier => "bracket identifier",
        LexicalState.BlockComment => "block comment",
        _ => "lexical region"
    };

    private enum LexicalState
    {
        Code,
        String,
        QuotedIdentifier,
        BracketIdentifier,
        LineComment,
        BlockComment
    }
}
