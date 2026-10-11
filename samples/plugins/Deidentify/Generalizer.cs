using System.Globalization;
using System.Text.RegularExpressions;

namespace DataPuddle.Deidentify;

/// <summary>
/// The Generalize action: reduces dates, ages and ZIP codes to what the HIPAA Safe Harbor method allows
/// (45 CFR 164.514(b)(2)). Every method returns null when the input cannot be read with certainty, so a
/// value that is not understood never passes through.
/// </summary>
internal static class Generalizer {
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // 2024-03-17, 2024/03/17, 2024-03-17T10:22:00, 2024-03-17 10:22:00
    private static readonly Regex IsoDate = new Regex(@"^\s*([0-9]{4})[-/.][0-9]{1,2}[-/.][0-9]{1,2}(?![0-9])", RegexOptions.Compiled, RegexTimeout);

    // 3/17/2024, 03-17-2024
    private static readonly Regex FullYearDate = new Regex(@"^\s*[0-9]{1,2}[-/.][0-9]{1,2}[-/.]([0-9]{4})(?![0-9])", RegexOptions.Compiled, RegexTimeout);

    // 3/17/24 is rejected: a two-digit year is ambiguous.
    private static readonly Regex TwoDigitYearDate = new Regex(@"^\s*[0-9]{1,2}[-/.][0-9]{1,2}[-/.][0-9]{2}(?![0-9])", RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex BareYear = new Regex(@"^\s*([0-9]{4})\s*$", RegexOptions.Compiled, RegexTimeout);

    // 12345, 12345-6789, 123456789, 12345 6789
    private static readonly Regex ZipPattern = new Regex(@"^\s*([0-9]{3})[0-9]{2}(?:[-\s]?[0-9]{4})?\s*$", RegexOptions.Compiled, RegexTimeout);

    /// <summary>The year of a date or timestamp written as text, or null when it cannot be read.</summary>
    public static bool TryGetYear(string? value, out int year) {
        year = 0;
        if (string.IsNullOrWhiteSpace(value)) {
            return false;
        }

        Match match = IsoDate.Match(value);
        if (!match.Success) {
            match = FullYearDate.Match(value);
        }
        if (!match.Success) {
            match = BareYear.Match(value);
        }
        if (match.Success) {
            year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return year >= 1000;
        }

        if (TwoDigitYearDate.IsMatch(value)) {
            return false;
        }

        // Written-out dates such as "March 17, 2024".
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed)) {
            year = parsed.Year;
            return true;
        }
        return false;
    }

    /// <summary>The year of a date as text ("2024"), or null.</summary>
    public static string? Year(string? value) {
        if (TryGetYear(value, out int year)) {
            return year.ToString(CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>
    /// The year of birth, or "90+" when the person is 90 or older. Safe Harbor requires ages over 89 and every
    /// date element that would reveal such an age to be combined into one category. The comparison uses years only,
    /// so someone who is 89 and close to turning 90 can also be placed in the 90+ group, which is allowed.
    /// <paramref name="asOf"/> is the reference date; null or empty means today (UTC).
    /// </summary>
    public static string? BirthYear(string? dateOfBirth, string? asOf) {
        if (!TryGetYear(dateOfBirth, out int birthYear)) {
            return null;
        }

        int referenceYear;
        if (string.IsNullOrWhiteSpace(asOf)) {
            referenceYear = DateTime.UtcNow.Year;
        } else if (!TryGetYear(asOf, out referenceYear)) {
            return null;
        }

        if (referenceYear - birthYear >= 90) {
            return "90+";
        }
        return birthYear.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>An age in years. Ages of 90 and over become 90, which stands for "90 or older". Negative ages give null.</summary>
    public static long? Age(long? age) {
        if (age == null || age.Value < 0) {
            return null;
        }
        if (age.Value >= 90) {
            return 90;
        }
        return age;
    }

    /// <summary>
    /// The first three digits of a ZIP code, or "000" when that three-digit area is in <paramref name="restricted"/>
    /// (areas with 20,000 people or fewer). Returns null when the value is not a five-digit ZIP or ZIP+4.
    /// </summary>
    public static string? Zip(string? value, ISet<string> restricted) {
        if (string.IsNullOrWhiteSpace(value)) {
            return null;
        }
        Match match = ZipPattern.Match(value);
        if (!match.Success) {
            return null;
        }
        string prefix = match.Groups[1].Value;
        if (restricted.Contains(prefix)) {
            return "000";
        }
        return prefix;
    }
}
