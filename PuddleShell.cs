using System.Data.Common;
using System.Globalization;
using System.Text;
using DuckDB.NET.Data;

namespace DataPuddle;

/// <summary>
/// A small interactive SQL prompt over the open local database connection.
/// </summary>
public static class PuddleShell {
    private const int MaxDisplayRows = 1000;
    private const int MaxColumnWidth = 60;

    public static void Run(LocalStore store) {
        DuckDBConnection duck = store.Connection;
        Console.WriteLine("╔════════════════════════════════════════════════╗");
        Console.WriteLine("║      01000100 01100001 01110100 01100001       ║");
        Console.WriteLine("║   █▀▄ █▀█ ▀█▀ █▀█   █▀█ █ █ █▀▄ █▀▄ █   █▀▀    ║");
        Console.WriteLine("║   █ █ █▀█  █  █▀█   █▀▀ █▄█ █ █ █ █ █▄▄ ██▄    ║");
        Console.WriteLine("║   ▀▀  ▀ ▀  ▀  ▀ ▀   ▀   ▀▀▀ ▀▀  ▀▀  ▀▀▀ ▀▀▀    ║");
        Console.WriteLine("║010100000111010101100100011001000110110001100101║");
        Console.WriteLine("╚════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("DataPuddle interactive shell (changes affect the local database only; use '.help' for help)");

        StringBuilder buffer = new StringBuilder();
        while (true) {
            Console.Write(buffer.Length == 0 ? "puddle> " : "   ...> ");
            string? line = Console.ReadLine();
            if (line == null) {
                break;
            }

            string trimmed = line.Trim();
            if (buffer.Length == 0 && IsCommand(trimmed, CloneKeyword)) {
                RunClone(store, trimmed);
                continue;
            }

            if (buffer.Length == 0 && IsCommand(trimmed, ExportKeyword)) {
                RunExport(store, trimmed);
                continue;
            }

            if (buffer.Length == 0 && trimmed.StartsWith('.')) {
                if (!HandleDotCommand(duck, trimmed)) {
                    break;
                }
                continue;
            }

            if (buffer.Length == 0 && trimmed.Length == 0) {
                continue;
            }

            buffer.AppendLine(line);
            if (trimmed.EndsWith(';')) {
                string sqlText = buffer.ToString();
                buffer.Clear();
                try {
                    string translated;
                    string? schemaToCreate;
                    if (SelectIntoTranslator.TryTranslate(sqlText, out translated, out schemaToCreate)) {
                        Console.WriteLine("-- translated to: " + translated.Trim());
                        if (schemaToCreate != null) {
                            store.Execute("CREATE SCHEMA IF NOT EXISTS \"" + schemaToCreate.Replace("\"", "\"\"") + "\"");
                        }
                        sqlText = translated;
                    }
                    ExecuteAndPrint(duck, sqlText);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: " + ex.Message);
                }
            }
        }
    }

    private const string CloneKeyword = ".clone";

    private const string ExportKeyword = ".export";

    private static bool IsCommand(string trimmed, string keyword) {
        if (!trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        return trimmed.Length == keyword.Length || char.IsWhiteSpace(trimmed[keyword.Length]);
    }

    // .export <delta|csv|tsv|delimited <char>> <schema.table>[, <schema.table> ...]
    // Exports tables that already exist in the local database (cloned or created with your own SQL).
    // Single line, optional trailing ';'. The delimiter may be quoted, written as \t, or the word tab.
    private static void RunExport(LocalStore store, string command) {
        const string usage =
            "Usage: .export <delta|csv|tsv|delimited <char>> <schema.table>[, <schema.table> ...]   e.g. .export csv main.customer_clean";

        string rest = command.Substring(ExportKeyword.Length).Trim().TrimEnd(';').Trim();
        if (rest.Length == 0) {
            Console.WriteLine(usage);
            return;
        }

        string formatToken = ReadToken(ref rest);
        ExportFormat format;
        char? delimiter = null;
        switch (formatToken.ToLowerInvariant()) {
            case "delta":
                format = ExportFormat.Delta;
                break;
            case "csv":
                format = ExportFormat.Csv;
                break;
            case "tsv":
                format = ExportFormat.Tsv;
                break;
            case "delimited": {
                format = ExportFormat.Delimited;
                string delimiterToken = ReadToken(ref rest);
                char parsedDelimiter;
                if (!TryParseDelimiter(delimiterToken, out parsedDelimiter)) {
                    Console.Error.WriteLine(
                        "Error: the delimited format needs a single delimiter character before the table names, " +
                        "for example: .export delimited | main.out   (use \\t or tab for a tab, or quote it: ';' )");
                    return;
                }
                delimiter = parsedDelimiter;
                break;
            }
            default:
                Console.Error.WriteLine($"Error: unknown export format '{formatToken}'. Use delta, csv, tsv or delimited <char>.");
                return;
        }

        if (rest.Length == 0) {
            Console.WriteLine(usage);
            return;
        }

        List<(string Schema, string Table)> tables;
        try {
            tables = LocalStore.ParseTableList(rest);
        } catch (FormatException ex) {
            Console.Error.WriteLine("Error: " + ex.Message);
            return;
        }

        Action<string> previousLog = store.Log;
        store.Log = Console.WriteLine;
        try {
            int failed = 0;
            foreach ((string Schema, string Table) t in tables) {
                try {
                    ExportResult result = store.Export(format, t.Schema, t.Table, delimiter);
                    if (!result.CountsMatch) {
                        failed++;
                    }
                } catch (Exception ex) {
                    failed++;
                    Console.Error.WriteLine($"Error exporting {t.Schema}.{t.Table}: {ex.Message}");
                }
            }
            Console.WriteLine(failed == 0
                ? $"Exported {tables.Count} table(s)."
                : $"Exported {tables.Count - failed} of {tables.Count} table(s) cleanly; {failed} had errors or count mismatches.");
        } finally {
            store.Log = previousLog;
        }
    }

    // Reads the next token from the front of text: a quoted string ('x' or "x") or a run of
    // non-whitespace characters. The consumed token is removed from text.
    private static string ReadToken(ref string text) {
        text = text.TrimStart();
        if (text.Length == 0) {
            return "";
        }

        char first = text[0];
        if (first == '\'' || first == '"') {
            int close = text.IndexOf(first, 1);
            if (close > 0) {
                string quoted = text.Substring(1, close - 1);
                text = text.Substring(close + 1).TrimStart();
                return quoted;
            }
        }

        int end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) {
            end++;
        }
        string token = text.Substring(0, end);
        text = text.Substring(end).TrimStart();
        return token;
    }

    internal static bool TryParseDelimiter(string token, out char delimiter) {
        if (string.Equals(token, "tab", StringComparison.OrdinalIgnoreCase) || token == "\\t") {
            delimiter = '\t';
            return true;
        }
        if (token.Length == 1) {
            delimiter = token[0];
            return true;
        }
        delimiter = '\0';
        return false;
    }

    // .clone <schema.table>[, <schema.table> ...]  - single line, optional trailing ';'.
    private static void RunClone(LocalStore store, string command) {
        string argument = command.Substring(CloneKeyword.Length).Trim().TrimEnd(';').Trim();
        if (argument.Length == 0) {
            Console.WriteLine("Usage: .clone <schema.table>[, <schema.table> ...]   e.g. .clone dbo.customer, dbo.invoice");
            return;
        }

        List<(string Schema, string Table)> tables;
        try {
            tables = LocalStore.ParseTableList(argument);
        } catch (FormatException ex) {
            Console.Error.WriteLine("Error: " + ex.Message);
            return;
        }

        Action<string> previousLog = store.Log;
        store.Log = Console.WriteLine;
        try {
            int failed = 0;
            foreach ((string Schema, string Table) t in tables) {
                try {
                    TableCopyResult result = store.CopyTable(t.Schema, t.Table);
                    if (!result.CountsMatch) {
                        failed++;
                    }
                } catch (Exception ex) {
                    failed++;
                    Console.Error.WriteLine($"Error cloning {t.Schema}.{t.Table}: {ex.Message}");
                }
            }
            Console.WriteLine(failed == 0
                ? $"Cloned {tables.Count} table(s)."
                : $"Cloned {tables.Count - failed} of {tables.Count} table(s) cleanly; {failed} had errors or count mismatches.");
        } finally {
            store.Log = previousLog;
        }
    }

    // Returns false when the shell should exit.
    private static bool HandleDotCommand(DuckDBConnection duck, string command) {
        string[] parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string name = parts[0].ToLowerInvariant();

        try {
            switch (name) {
                case ".quit":
                case ".exit":
                case ".q":
                    return false;
                case ".tables":
                    ExecuteAndPrint(duck,
                        "SELECT table_schema, table_name FROM information_schema.tables " +
                        "WHERE table_schema NOT IN ('information_schema', 'pg_catalog') ORDER BY 1, 2;");
                    break;
                case ".schema":
                    if (parts.Length < 2) {
                        Console.WriteLine("Usage: .schema <table>   e.g. .schema dbo.customer");
                    } else {
                        ExecuteAndPrint(duck, $"DESCRIBE {parts[1]};");
                    }
                    break;
                case ".help":
                    Console.WriteLine("End SQL statements with ';'.");
                    Console.WriteLine("Commands:");
                    Console.WriteLine("     .clone <schema.table>[, ...]");
                    Console.WriteLine("     .export <delta|csv|tsv|delimited <char>> <schema.table>[, ...]");
                    Console.WriteLine("     .tables");
                    Console.WriteLine("     .schema <table>");
                    Console.WriteLine("     .help");
                    Console.WriteLine("     .quit");
                    break;
                default:
                    Console.WriteLine($"Unknown command '{name}'. Type .help for the list.");
                    break;
            }
        } catch (Exception ex) {
            Console.Error.WriteLine("Error: " + ex.Message);
        }
        return true;
    }

    private static void ExecuteAndPrint(DuckDBConnection duck, string sqlText) {
        using (DuckDBCommand cmd = duck.CreateCommand()) {
            cmd.CommandText = sqlText;
            using (DbDataReader reader = cmd.ExecuteReader()) {
                if (reader.FieldCount == 0) {
                    Console.WriteLine("OK");
                    return;
                }

                int fieldCount = reader.FieldCount;
                string[] headers = new string[fieldCount];
                for (int i = 0; i < fieldCount; i++) {
                    headers[i] = reader.GetName(i);
                }

                List<string[]> rows = new List<string[]>();
                bool truncated = false;
                while (reader.Read()) {
                    if (rows.Count >= MaxDisplayRows) {
                        truncated = true;
                        break;
                    }
                    string[] cells = new string[fieldCount];
                    for (int i = 0; i < fieldCount; i++) {
                        cells[i] = FormatCell(reader, i);
                    }
                    rows.Add(cells);
                }

                int[] widths = new int[fieldCount];
                for (int i = 0; i < fieldCount; i++) {
                    widths[i] = headers[i].Length;
                }
                foreach (string[] row in rows) {
                    for (int i = 0; i < fieldCount; i++) {
                        widths[i] = Math.Max(widths[i], row[i].Length);
                    }
                }
                for (int i = 0; i < fieldCount; i++) {
                    widths[i] = Math.Min(widths[i], MaxColumnWidth);
                }

                Console.WriteLine(FormatLine(headers, widths));
                Console.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));
                foreach (string[] row in rows) {
                    Console.WriteLine(FormatLine(row, widths));
                }

                Console.WriteLine(truncated
                    ? $"({rows.Count:N0} rows shown; more rows not displayed)"
                    : $"({rows.Count:N0} rows)");
            }
        }
    }

    private static string FormatCell(DbDataReader reader, int index) {
        if (reader.IsDBNull(index)) {
            return "NULL";
        }
        object value = reader.GetValue(index);
        if (value is byte[] bytes) {
            return "0x" + Convert.ToHexString(bytes);
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static string FormatLine(string[] cells, int[] widths) {
        List<string> padded = new List<string>();
        for (int i = 0; i < cells.Length; i++) {
            string text = cells[i].Replace('\r', ' ').Replace('\n', ' ');
            if (text.Length > widths[i]) {
                text = text.Substring(0, widths[i] - 1) + "~";
            }
            padded.Add(text.PadRight(widths[i]));
        }
        return string.Join(" | ", padded);
    }
}
