using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DataPuddle.Deidentify;

/// <summary>
/// The Scan action: finds identifiers inside free text and replaces each with a token such as [SSN].
/// Three passes run in order: patterns (URL, email, SSN, phone, IP address, dates), a list of known terms from a
/// file (names of patients, providers and facilities exported from the database), and terms passed for one row
/// (for example that customer's own name).
///
/// This is a best-effort filter. Pattern and word-list matching misses misspelled names, nicknames, and anything
/// not on the list, so free text that must be safe should be removed or reviewed instead.
/// </summary>
internal sealed class TextScanner {
    private const string Months = "(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)";
    private const int MaxCachedRowTerms = 5000;
    private const string Failed = "[REDACTED]";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly (Regex Pattern, string Token)[] Patterns = new (Regex, string)[] {
        (Make(@"\b(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase), "[URL]"),
        (Make(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.None), "[EMAIL]"),
        (Make(@"(?<!\d)\d{3}[- ]\d{2}[- ]\d{4}(?!\d)", RegexOptions.None), "[SSN]"),
        (Make(@"(?<!\d)(?:\+?1[\s.\-]?)?(?:\(\d{3}\)|\d{3})[\s.\-]?\d{3}[\s.\-]?\d{4}(?!\d)", RegexOptions.None), "[PHONE]"),
        (Make(@"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)", RegexOptions.None), "[IP]"),
        (Make(@"(?<!\d)(?:\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}|\d{4}[/\-.]\d{1,2}[/\-.]\d{1,2})(?!\d)", RegexOptions.None), "[DATE]"),
        (Make(@"\b" + Months + @"\.?\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})?\b", RegexOptions.IgnoreCase), "[DATE]"),
        (Make(@"\b\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?" + Months + @"\.?(?:,?\s+\d{4})?\b", RegexOptions.IgnoreCase), "[DATE]")
    };

    private readonly Regex? _knownTerms;
    private readonly ConcurrentDictionary<string, Regex?> _rowTerms = new ConcurrentDictionary<string, Regex?>(StringComparer.Ordinal);

    /// <summary>Creates a scanner. <paramref name="knownTerms"/> are words and phrases to replace with [NAME] everywhere.</summary>
    public TextScanner(IEnumerable<string> knownTerms) {
        _knownTerms = BuildTermsPattern(knownTerms, RegexOptions.Compiled);
    }

    /// <summary>Redacts <paramref name="text"/>. <paramref name="rowTerms"/> is an optional list of terms separated by '|'.</summary>
    public string? Scan(string? text, string? rowTerms) {
        if (string.IsNullOrEmpty(text)) {
            return text;
        }

        try {
            string result = text;
            foreach ((Regex pattern, string token) in Patterns) {
                result = pattern.Replace(result, token);
            }
            if (_knownTerms != null) {
                result = _knownTerms.Replace(result, "[NAME]");
            }
            if (!string.IsNullOrEmpty(rowTerms)) {
                Regex? perRow = _rowTerms.GetOrAdd(rowTerms, BuildRowPattern);
                if (perRow != null) {
                    result = perRow.Replace(result, "[NAME]");
                }
            }
            return result;
        } catch (RegexMatchTimeoutException) {
            // If the scan could not finish, nothing from the text is released.
            return Failed;
        }
    }

    private Regex? BuildRowPattern(string rowTerms) {
        if (_rowTerms.Count > MaxCachedRowTerms) {
            _rowTerms.Clear();
        }
        return BuildTermsPattern(rowTerms.Split('|'), RegexOptions.None);
    }

    private static Regex? BuildTermsPattern(IEnumerable<string> terms, RegexOptions options) {
        List<string> cleaned = terms
            .Select((string term) => term.Trim())
            .Where((string term) => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending((string term) => term.Length)
            .Select((string term) => Regex.Escape(term))
            .ToList();
        if (cleaned.Count == 0) {
            return null;
        }

        string pattern = @"(?<![\p{L}\p{N}])(?:" + string.Join("|", cleaned) + @")(?![\p{L}\p{N}])";
        return new Regex(pattern, options | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static Regex Make(string pattern, RegexOptions options) {
        return new Regex(pattern, options | RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);
    }
}
