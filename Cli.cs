using Spectre.Console;

namespace DataPuddle;

/// <summary>
/// Console output with color, built on Spectre.Console. Color is used only when output goes to a terminal
/// that supports it; when output is redirected to a file or pipe, or NO_COLOR is set, plain text is written
/// exactly as before (no escape codes, no extra line wrapping).
/// </summary>
internal static class Cli {
    private const string TableNameStyle = "yellow";

    private static readonly IAnsiConsole StdOut = AnsiConsole.Console;
    private static readonly IAnsiConsole StdErr = AnsiConsole.Create(new AnsiConsoleSettings {
        Out = new AnsiConsoleOutput(Console.Error)
    });

    /// <summary>
    /// The sink for progress messages (assign it to <see cref="LocalStore.Log"/>). It colors table names
    /// in the "== schema.table ==" headings yellow, row-count verdicts green or red, and problems red.
    /// </summary>
    public static void Log(string line) {
        if (!ColorEnabled(StdOut, Console.IsOutputRedirected)) {
            Console.WriteLine(line);
            return;
        }
        StdOut.MarkupLine(ToMarkup(line));
    }

    /// <summary>A line in the given Spectre style, for example "green" or "bold red".</summary>
    public static void Line(string text, string style) {
        if (!ColorEnabled(StdOut, Console.IsOutputRedirected)) {
            Console.WriteLine(text);
            return;
        }
        StdOut.MarkupLine($"[{style}]{Markup.Escape(text)}[/]");
    }

    public static void Success(string text) {
        Line(text, "green");
    }

    public static void Warning(string text) {
        Line(text, "darkorange");
    }

    /// <summary>Writes to standard error in red.</summary>
    public static void Error(string text) {
        if (!ColorEnabled(StdErr, Console.IsErrorRedirected)) {
            Console.Error.WriteLine(text);
            return;
        }
        StdErr.MarkupLine($"[red]{Markup.Escape(text)}[/]");
    }

    /// <summary>
    /// A row such as "  dbo.customer         1.2 s": the label (a table name, yellow by default) is padded
    /// to <paramref name="width"/>, then the value.
    /// </summary>
    public static void TableRow(string label, string value, int width, string labelStyle = TableNameStyle) {
        string padded = label.PadRight(width);
        if (!ColorEnabled(StdOut, Console.IsOutputRedirected)) {
            Console.WriteLine($"  {padded} {value}");
            return;
        }
        StdOut.MarkupLine($"  [{labelStyle}]{Markup.Escape(padded)}[/] {Markup.Escape(value)}");
    }

    private static bool ColorEnabled(IAnsiConsole console, bool redirected) {
        return !redirected && console.Profile.Capabilities.ColorSystem != ColorSystem.NoColors;
    }

    private static string ToMarkup(string line) {
        // "== schema.table ==" and "== export csv schema.table ==": the table name is the last word.
        if (line.StartsWith("== ", StringComparison.Ordinal) && line.EndsWith(" ==", StringComparison.Ordinal) && line.Length > 6) {
            string inner = line.Substring(3, line.Length - 6);
            int split = inner.LastIndexOf(' ');
            string lead = split >= 0 ? inner.Substring(0, split + 1) : string.Empty;
            string name = split >= 0 ? inner.Substring(split + 1) : inner;
            return $"== {Markup.Escape(lead)}[{TableNameStyle}]{Markup.Escape(name)}[/] ==";
        }

        if (line.EndsWith("  OK", StringComparison.Ordinal)) {
            return Markup.Escape(line.Substring(0, line.Length - 2)) + "[green]OK[/]";
        }
        if (line.EndsWith("MISMATCH", StringComparison.Ordinal)) {
            return Markup.Escape(line.Substring(0, line.Length - 8)) + "[bold red]MISMATCH[/]";
        }

        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("could not", StringComparison.OrdinalIgnoreCase) || trimmed.Contains(" failed", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("was not registered", StringComparison.OrdinalIgnoreCase)) {
            return $"[red]{Markup.Escape(line)}[/]";
        }
        if (trimmed.StartsWith("note:", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("were not inherited", StringComparison.OrdinalIgnoreCase)) {
            return $"[darkorange]{Markup.Escape(line)}[/]";
        }
        if (trimmed.StartsWith("Plugin loaded", StringComparison.Ordinal)) {
            return $"[green]{Markup.Escape(line)}[/]";
        }
        if (trimmed.StartsWith("elapsed ", StringComparison.Ordinal)) {
            return $"[grey]{Markup.Escape(line)}[/]";
        }
        return Markup.Escape(line);
    }
}
