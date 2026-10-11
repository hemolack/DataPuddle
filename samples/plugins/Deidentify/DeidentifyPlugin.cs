using DataPuddle.Plugins;

namespace DataPuddle.Deidentify;

/// <summary>
/// De-identification functions for SQL, one per action: Remove, Generalize, Pseudonymize, Scan and Keep.
///
///   CREATE TABLE deid.customer AS
///   SELECT deid_pseudonym(customer_id, 'customer_id') AS customer_id,
///          deid_birth_year(CAST(birth_date AS VARCHAR)) AS birth_year,
///          deid_zip(zip) AS zip3,
///          deid_scan(notes) AS notes
///   FROM dbo.customer;
///
/// Settings (Plugins:Settings:Deidentify):
///   MappingFile          Where pseudonym pairs are saved so surrogates stay the same between runs. PHI: protect it.
///                        Without it the pairs live in memory and are lost when the session ends.
///   RestrictedZip3       Three-digit ZIP prefixes whose area has 20,000 people or fewer, separated by commas or spaces.
///   RestrictedZip3File   The same list in a text file. Either setting turns on deid_zip; neither leaves it unregistered.
///   ScanTermsFile        One term per line (patient, provider and facility names, # for comments) that deid_scan
///                        replaces with [NAME].
/// </summary>
public sealed class DeidentifyPlugin : IPuddlePlugin {
    public string Name {
        get {
            return "Deidentify";
        }
    }

    public string Description {
        get {
            return "deid_remove, deid_year, deid_birth_year, deid_age, deid_zip, deid_pseudonym, deid_scan, deid_keep (HIPAA Safe Harbor)";
        }
    }

    public void Initialize(IPluginContext context) {
        // ---------- Remove ----------
        context.RegisterStringFunction("deid_remove", (string? value) => null,
            "Remove: always NULL. Leave the column out of the output table when you can; use this to keep the column but empty.");

        // ---------- Keep ----------
        context.RegisterStringFunction("deid_keep", (string? value) => value,
            "Keep: returns the text unchanged, marking a column a reviewer has cleared of Safe Harbor identifiers.");

        // ---------- Generalize ----------
        context.RegisterStringFunction("deid_year", (string? value) => Generalizer.Year(value),
            "Generalize: the year of a date given as text, for example '2024'. NULL if the date cannot be read.");
        context.RegisterStringFunction("deid_birth_year", (string? dateOfBirth) => Generalizer.BirthYear(dateOfBirth, null),
            "Generalize: the year of birth, or '90+' for people 90 or older as of today.");
        context.RegisterStringFunction("deid_birth_year_as_of", (string? dateOfBirth, string? asOf) => Generalizer.BirthYear(dateOfBirth, asOf),
            "Generalize: the year of birth, or '90+' for people 90 or older as of the second argument (a date as text).");
        context.RegisterIntegerFunction("deid_age", (long? age) => Generalizer.Age(age),
            "Generalize: an age in years; 90 and over become 90, meaning 90 or older.");

        HashSet<string>? restricted = LoadRestrictedZip3(context);
        if (restricted != null) {
            context.RegisterStringFunction("deid_zip", (string? zip) => Generalizer.Zip(zip, restricted),
                "Generalize: first three digits of a ZIP code, or '000' for a restricted area. NULL if not a five-digit ZIP or ZIP+4.");
        } else {
            context.Log("deid_zip is not registered: set RestrictedZip3 or RestrictedZip3File to the list of three-digit ZIP prefixes with 20,000 people or fewer.");
        }

        // ---------- Pseudonymize ----------
        string? mappingFile = null;
        if (context.Settings.TryGetValue("MappingFile", out string? mappingSetting) && !string.IsNullOrWhiteSpace(mappingSetting)) {
            mappingFile = Path.GetFullPath(mappingSetting, context.WorkingDirectory);
        } else {
            context.Log("MappingFile is not set: pseudonyms are kept in memory only and are lost when the session ends.");
        }
        PseudonymMap map = new PseudonymMap(mappingFile);
        context.RegisterStringFunction("deid_pseudonym", (string? value, string? domain) => map.Pseudonymize(value, domain),
            "Pseudonymize: a random surrogate for an identifier. The second argument names the identifier type, e.g. deid_pseudonym(mrn, 'mrn').");

        // ---------- Scan ----------
        List<string> knownTerms = new List<string>();
        if (context.Settings.TryGetValue("ScanTermsFile", out string? termsSetting) && !string.IsNullOrWhiteSpace(termsSetting)) {
            string termsFile = Path.GetFullPath(termsSetting, context.WorkingDirectory);
            foreach (string line in File.ReadLines(termsFile)) {
                string term = line.Trim();
                if (term.Length > 0 && !term.StartsWith('#')) {
                    knownTerms.Add(term);
                }
            }
            context.Log($"deid_scan will redact {knownTerms.Count} known terms from {termsFile}.");
        }
        TextScanner scanner = new TextScanner(knownTerms);
        context.RegisterStringFunction("deid_scan", (string? text) => scanner.Scan(text, null),
            "Scan: replaces SSNs, phone numbers, emails, URLs, IP addresses, dates and known names in free text with tokens like [SSN]. Best effort.");
        context.RegisterStringFunction("deid_scan_terms", (string? text, string? terms) => scanner.Scan(text, terms),
            "Scan: like deid_scan, and also replaces the terms in the second argument (separated by '|') with [NAME], e.g. the row's own patient name.");
    }

    private static HashSet<string>? LoadRestrictedZip3(IPluginContext context) {
        List<string> entries = new List<string>();
        bool configured = false;

        if (context.Settings.TryGetValue("RestrictedZip3", out string? listSetting) && !string.IsNullOrWhiteSpace(listSetting)) {
            configured = true;
            entries.AddRange(listSetting.Split(new char[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }
        if (context.Settings.TryGetValue("RestrictedZip3File", out string? fileSetting) && !string.IsNullOrWhiteSpace(fileSetting)) {
            configured = true;
            string path = Path.GetFullPath(fileSetting, context.WorkingDirectory);
            foreach (string line in File.ReadLines(path)) {
                if (line.TrimStart().StartsWith('#')) {
                    continue;
                }
                entries.AddRange(line.Split(new char[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }
        if (!configured) {
            return null;
        }

        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in entries) {
            if (entry.Length != 3 || !entry.All(char.IsAsciiDigit)) {
                throw new InvalidOperationException($"'{entry}' is not a three-digit ZIP prefix.");
            }
            result.Add(entry);
        }
        return result;
    }
}
