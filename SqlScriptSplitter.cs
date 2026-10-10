using System.Text;

namespace DataPuddle;

/// <summary>
/// Splits a SQL script into individual statements at semicolons. Semicolons inside 'strings',
/// "quoted identifiers", -- line comments and /* block comments */ do not split. Fragments that hold
/// only whitespace or comments are dropped. The terminating semicolon is not included.
/// </summary>
public static class SqlScriptSplitter {
    public static List<string> Split(string script) {
        List<string> statements = new List<string>();
        StringBuilder current = new StringBuilder();
        bool hasContent = false;
        int i = 0;
        while (i < script.Length) {
            char c = script[i];
            bool hasNext = i + 1 < script.Length;
            if (c == '-' && hasNext && script[i + 1] == '-') {
                int end = script.IndexOf('\n', i);
                if (end < 0) {
                    end = script.Length;
                }
                current.Append(script, i, end - i);
                i = end;
            } else if (c == '/' && hasNext && script[i + 1] == '*') {
                int close = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int end = close < 0 ? script.Length : close + 2;
                current.Append(script, i, end - i);
                i = end;
            } else if (c == '\'' || c == '"') {
                int end = SkipQuoted(script, i, c);
                current.Append(script, i, end - i);
                hasContent = true;
                i = end;
            } else if (c == ';') {
                AddStatement(statements, current, hasContent);
                hasContent = false;
                i++;
            } else {
                current.Append(c);
                if (!char.IsWhiteSpace(c)) {
                    hasContent = true;
                }
                i++;
            }
        }
        AddStatement(statements, current, hasContent);
        return statements;
    }

    private static void AddStatement(List<string> statements, StringBuilder current, bool hasContent) {
        if (hasContent) {
            statements.Add(current.ToString().Trim());
        }
        current.Clear();
    }

    // Returns the index just past the closing quote. A doubled quote inside is an escaped quote.
    private static int SkipQuoted(string text, int start, char quote) {
        int i = start + 1;
        while (i < text.Length) {
            if (text[i] == quote) {
                if (i + 1 < text.Length && text[i + 1] == quote) {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }
}
