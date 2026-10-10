namespace DataPuddle;

/// <summary>
/// DuckDB has no SELECT ... INTO. This rewrites the T-SQL idiom
/// <c>SELECT cols INTO newtable FROM ...</c> into <c>CREATE TABLE newtable AS SELECT cols FROM ...</c>.
/// Macros cannot do this because DuckDB macros hold expressions or queries, not statement rewrites,
/// so it is done as a text translation before the statement is sent to DuckDB.
/// </summary>
/// <remarks>
/// Only the first statement of the text is considered. The statement must start with SELECT or WITH
/// (so INSERT INTO ... SELECT is never touched). The target may be written as name, schema.name or
/// catalog.schema.name, using bare words, "double quotes" or [square brackets]. Strings, quoted
/// identifiers, comments and parenthesized sub-queries are skipped when looking for INTO.
/// Not translated: temp tables written with a leading #, and SELECT TOP.
/// </remarks>
public static class SelectIntoTranslator {
    private enum TokenKind {
        Word,
        Literal,
        QuotedIdentifier,
        Symbol
    }

    private sealed record Token(TokenKind Kind, int Start, int End);

    /// <summary>
    /// Returns true and the translated SQL when <paramref name="sql"/> is a SELECT ... INTO statement.
    /// <paramref name="schema"/> is the schema named in a two-part target (so the caller can create it
    /// first), or null. When nothing needs translating, returns false and leaves the text unchanged.
    /// </summary>
    public static bool TryTranslate(string sql, out string translatedSql, out string? schema) {
        translatedSql = sql;
        schema = null;

        List<Token> tokens = Tokenize(sql);
        if (tokens.Count == 0 || tokens[0].Kind != TokenKind.Word) {
            return false;
        }

        string firstWord = TextOf(sql, tokens[0]).ToUpperInvariant();
        if (firstWord != "SELECT" && firstWord != "WITH") {
            return false;
        }

        int depth = 0;
        bool sawSelect = firstWord == "SELECT";
        int intoIndex = -1;
        for (int t = 0; t < tokens.Count; t++) {
            Token token = tokens[t];
            if (token.Kind == TokenKind.Symbol) {
                char symbol = sql[token.Start];
                if (symbol == '(' || symbol == '[') {
                    depth++;
                } else if (symbol == ')' || symbol == ']') {
                    if (depth > 0) {
                        depth--;
                    }
                } else if (symbol == ';' && depth == 0) {
                    break;
                }
                continue;
            }

            if (token.Kind != TokenKind.Word || depth != 0) {
                continue;
            }

            string word = TextOf(sql, token).ToUpperInvariant();
            if (word == "SELECT") {
                sawSelect = true;
            } else if (word == "INSERT" || word == "UPDATE" || word == "DELETE" || word == "MERGE" || word == "CREATE" || word == "COPY") {
                return false;
            } else if (word == "FROM") {
                // In SELECT ... INTO the INTO clause comes before FROM, so there is nothing to translate.
                return false;
            } else if (word == "INTO") {
                if (!sawSelect) {
                    return false;
                }
                intoIndex = t;
                break;
            }
        }

        if (intoIndex < 0) {
            return false;
        }

        int intoStart = tokens[intoIndex].Start;
        int position = tokens[intoIndex].End;
        List<string> parts = new List<string>();
        if (!TryParseName(sql, ref position, parts)) {
            return false;
        }

        string quotedName = string.Join(".", parts.Select(p => "\"" + p.Replace("\"", "\"\"") + "\""));
        string withoutInto = sql.Remove(intoStart, position - intoStart);
        translatedSql = withoutInto.Insert(tokens[0].Start, $"CREATE TABLE {quotedName} AS ");
        schema = parts.Count == 2 ? parts[0] : null;
        return true;
    }

    // ---------- Scanning ----------

    private static string TextOf(string sql, Token token) {
        return sql.Substring(token.Start, token.End - token.Start);
    }

    private static List<Token> Tokenize(string sql) {
        List<Token> tokens = new List<Token>();
        int i = 0;
        while (i < sql.Length) {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) {
                i++;
            } else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') {
                while (i < sql.Length && sql[i] != '\n') {
                    i++;
                }
            } else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') {
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
            } else if (c == '\'' || c == '"') {
                int end = SkipQuoted(sql, i, c);
                tokens.Add(new Token(c == '\'' ? TokenKind.Literal : TokenKind.QuotedIdentifier, i, end));
                i = end;
            } else if (char.IsLetter(c) || c == '_') {
                int start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) {
                    i++;
                }
                tokens.Add(new Token(TokenKind.Word, start, i));
            } else {
                tokens.Add(new Token(TokenKind.Symbol, i, i + 1));
                i++;
            }
        }
        return tokens;
    }

    // Returns the index just past the closing quote. A doubled quote inside is an escaped quote.
    private static int SkipQuoted(string sql, int start, char quote) {
        int i = start + 1;
        while (i < sql.Length) {
            if (sql[i] == quote) {
                if (i + 1 < sql.Length && sql[i + 1] == quote) {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    // Returns the index of the closing ']' (a doubled ']' is an escaped bracket), or -1.
    private static int FindBracketEnd(string sql, int start) {
        int i = start + 1;
        while (i < sql.Length) {
            if (sql[i] == ']') {
                if (i + 1 < sql.Length && sql[i + 1] == ']') {
                    i += 2;
                    continue;
                }
                return i;
            }
            i++;
        }
        return -1;
    }

    // Parses name, schema.name or catalog.schema.name starting at position (leading whitespace is
    // skipped). On success position is left just past the name and the unquoted parts are returned.
    private static bool TryParseName(string sql, ref int position, List<string> parts) {
        int p = position;
        while (p < sql.Length && char.IsWhiteSpace(sql[p])) {
            p++;
        }

        while (true) {
            if (p >= sql.Length) {
                return false;
            }

            string part;
            char c = sql[p];
            if (c == '[') {
                int close = FindBracketEnd(sql, p);
                if (close < 0) {
                    return false;
                }
                part = sql.Substring(p + 1, close - p - 1).Replace("]]", "]");
                p = close + 1;
            } else if (c == '"') {
                int end = SkipQuoted(sql, p, '"');
                if (end - p < 2 || sql[end - 1] != '"') {
                    return false;
                }
                part = sql.Substring(p + 1, end - p - 2).Replace("\"\"", "\"");
                p = end;
            } else if (char.IsLetter(c) || c == '_') {
                int start = p;
                while (p < sql.Length && (char.IsLetterOrDigit(sql[p]) || sql[p] == '_' || sql[p] == '$')) {
                    p++;
                }
                part = sql.Substring(start, p - start);
            } else {
                return false;
            }

            if (part.Length == 0) {
                return false;
            }
            parts.Add(part);

            if (p < sql.Length && sql[p] == '.') {
                if (parts.Count >= 3) {
                    return false;
                }
                p++;
                continue;
            }
            break;
        }

        position = p;
        return true;
    }
}
