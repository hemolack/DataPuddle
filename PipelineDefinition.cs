using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataPuddle;

/// <summary>A problem in the pipeline file or in the pipeline settings in the config file.</summary>
public sealed class PipelineConfigException : Exception {
    public PipelineConfigException(string message) : base(message) {
    }
}

public enum PipelineStepKind {
    Sql,
    Clone,
    Export,
    Assert
}

/// <summary>One step of a pipeline, fully resolved: parameters substituted, files read, names validated.</summary>
public sealed class PipelineStep {
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public PipelineStepKind Kind { get; init; }

    /// <summary>Sql steps: the statements to run, in order.</summary>
    public IReadOnlyList<string> Statements { get; init; } = new List<string>();

    /// <summary>Sql steps that came from a file: the file name, for messages.</summary>
    public string? SourceFile { get; init; }

    /// <summary>Clone and export steps: the tables, as schema.table.</summary>
    public IReadOnlyList<string> Tables { get; init; } = new List<string>();

    /// <summary>When true, a failure of this step is recorded and the run carries on with the next step.</summary>
    public bool ContinueOnError { get; init; }

    public ExportFormat ExportFormat { get; init; }
    public char? Delimiter { get; init; }

    /// <summary>Assert steps: the query, and how many rows it must return (0 means it must return none).</summary>
    public string AssertQuery { get; init; } = "";
    public long ExpectRows { get; init; }

    public string KindName {
        get {
            return Kind.ToString().ToLowerInvariant();
        }
    }

    public string Describe() {
        string text = DescribeKind();
        return ContinueOnError ? text + ", continues on error" : text;
    }

    private string DescribeKind() {
        switch (Kind) {
            case PipelineStepKind.Sql:
                return $"sql, {Statements.Count} statement(s)" + (SourceFile != null ? $" from {SourceFile}" : "");
            case PipelineStepKind.Clone:
                return "clone " + string.Join(", ", Tables);
            case PipelineStepKind.Export:
                return $"export {ExportFormat.ToString().ToLowerInvariant()} " + string.Join(", ", Tables);
            case PipelineStepKind.Assert:
                return $"assert, expects {ExpectRows} row(s)";
            default:
                return KindName;
        }
    }
}

/// <summary>
/// A pipeline loaded from a JSON file: parameters plus an ordered list of steps. Loading validates
/// everything up front (unknown keys, bad table names, missing files, unresolved parameters) so a
/// mistake is reported before anything is cloned or run.
/// </summary>
public sealed class PipelineDefinition {
    public string Name { get; init; } = "";
    public string FilePath { get; init; } = "";

    /// <summary>The parameter values in effect (file defaults overridden by the command line).</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<PipelineStep> Steps { get; init; } = new List<PipelineStep>();

    private static readonly Regex ParameterPattern = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex ParameterNamePattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static readonly string[] KindKeys = { "sql", "file", "clone", "export", "assert" };

    /// <summary>
    /// Loads and validates the pipeline file. <paramref name="overrides"/> are parameter values from the
    /// command line; they replace the defaults in the file's "parameters" object.
    /// </summary>
    public static PipelineDefinition Load(string path, IReadOnlyDictionary<string, string> overrides) {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) {
            throw new PipelineConfigException($"Pipeline file not found: {fullPath}");
        }
        string directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        string label = Path.GetFileName(fullPath);

        JsonDocumentOptions documentOptions = new JsonDocumentOptions {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        JsonDocument document;
        try {
            document = JsonDocument.Parse(File.ReadAllText(fullPath), documentOptions);
        } catch (JsonException ex) {
            throw new PipelineConfigException($"{label} is not valid JSON: {ex.Message}");
        }

        using (document) {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                throw new PipelineConfigException($"{label}: the top level must be a JSON object.");
            }
            CheckKeys(root, new[] { "name", "parameters", "steps" }, label);

            string name = Path.GetFileNameWithoutExtension(fullPath);
            if (root.TryGetProperty("name", out JsonElement nameElement)) {
                name = ReadString(nameElement, $"{label}: \"name\"");
            }

            Dictionary<string, string> parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("parameters", out JsonElement parametersElement)) {
                if (parametersElement.ValueKind != JsonValueKind.Object) {
                    throw new PipelineConfigException($"{label}: \"parameters\" must be an object of name/value pairs.");
                }
                foreach (JsonProperty property in parametersElement.EnumerateObject()) {
                    parameters[property.Name] = ReadParameterValue(property.Value, $"{label}: parameter \"{property.Name}\"");
                }
            }
            foreach (KeyValuePair<string, string> pair in overrides) {
                parameters[pair.Key] = pair.Value;
            }
            foreach (string parameterName in parameters.Keys) {
                if (!ParameterNamePattern.IsMatch(parameterName)) {
                    throw new PipelineConfigException(
                        $"Parameter name '{parameterName}' is not valid. Use letters, digits and underscores, starting with a letter or underscore.");
                }
            }

            if (!root.TryGetProperty("steps", out JsonElement stepsElement) ||
                stepsElement.ValueKind != JsonValueKind.Array ||
                stepsElement.GetArrayLength() == 0) {
                throw new PipelineConfigException($"{label}: \"steps\" must be a non-empty array.");
            }

            HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<PipelineStep> steps = new List<PipelineStep>();
            int index = 0;
            foreach (JsonElement stepElement in stepsElement.EnumerateArray()) {
                index++;
                steps.Add(ParseStep(stepElement, index, label, directory, parameters, missing));
            }

            if (missing.Count > 0) {
                string names = string.Join(", ", missing.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).Select(m => "${" + m + "}"));
                throw new PipelineConfigException(
                    $"{label} uses parameters with no value: {names}. " +
                    "Pass them with -p name=value or give defaults in the \"parameters\" object.");
            }

            return new PipelineDefinition {
                Name = name,
                FilePath = fullPath,
                Parameters = parameters,
                Steps = steps
            };
        }
    }

    private static PipelineStep ParseStep(
        JsonElement element,
        int index,
        string label,
        string directory,
        Dictionary<string, string> parameters,
        HashSet<string> missing) {

        string where = $"{label}, step {index}";
        if (element.ValueKind != JsonValueKind.Object) {
            throw new PipelineConfigException($"{where}: each step must be a JSON object.");
        }

        List<string> kindsPresent = new List<string>();
        foreach (string key in KindKeys) {
            if (element.TryGetProperty(key, out JsonElement _)) {
                kindsPresent.Add(key);
            }
        }
        if (kindsPresent.Count != 1) {
            throw new PipelineConfigException(
                $"{where}: a step needs exactly one of \"sql\", \"file\", \"clone\", \"export\" or \"assert\"" +
                (kindsPresent.Count == 0 ? "." : $", but has {string.Join(", ", kindsPresent)}."));
        }
        string kindKey = kindsPresent[0];

        string stepName = $"step {index}";
        if (element.TryGetProperty("name", out JsonElement nameElement)) {
            stepName = ReadString(nameElement, $"{where}: \"name\"");
            where = $"{label}, step {index} ({stepName})";
        }

        bool continueOnError = false;
        if (element.TryGetProperty("continueOnError", out JsonElement continueElement)) {
            if (continueElement.ValueKind != JsonValueKind.True && continueElement.ValueKind != JsonValueKind.False) {
                throw new PipelineConfigException($"{where}: \"continueOnError\" must be true or false.");
            }
            continueOnError = continueElement.ValueKind == JsonValueKind.True;
        }

        JsonElement value = element.GetProperty(kindKey);
        switch (kindKey) {
            case "sql": {
                CheckKeys(element, new[] { "name", "continueOnError", "sql" }, where);
                List<string> statements = new List<string>();
                foreach (string text in ReadStringOrList(value, $"{where}: \"sql\"")) {
                    statements.AddRange(SqlScriptSplitter.Split(Substitute(text, parameters, missing)));
                }
                if (statements.Count == 0) {
                    throw new PipelineConfigException($"{where}: \"sql\" contains no statements.");
                }
                return new PipelineStep { Index = index, ContinueOnError = continueOnError, Name = stepName, Kind = PipelineStepKind.Sql, Statements = statements };
            }
            case "file": {
                CheckKeys(element, new[] { "name", "continueOnError", "file" }, where);
                string fileSetting = Substitute(ReadString(value, $"{where}: \"file\""), parameters, missing);
                string filePath = Path.IsPathRooted(fileSetting) ? fileSetting : Path.Combine(directory, fileSetting);
                if (!File.Exists(filePath)) {
                    throw new PipelineConfigException($"{where}: SQL file not found: {Path.GetFullPath(filePath)}");
                }
                List<string> statements = SqlScriptSplitter.Split(Substitute(File.ReadAllText(filePath), parameters, missing));
                if (statements.Count == 0) {
                    throw new PipelineConfigException($"{where}: {fileSetting} contains no statements.");
                }
                return new PipelineStep {
                    Index = index,
                    ContinueOnError = continueOnError,
                    Name = stepName,
                    Kind = PipelineStepKind.Sql,
                    Statements = statements,
                    SourceFile = fileSetting
                };
            }
            case "clone": {
                CheckKeys(element, new[] { "name", "continueOnError", "clone" }, where);
                List<string> tables = ReadTables(value, $"{where}: \"clone\"", parameters, missing);
                return new PipelineStep { Index = index, ContinueOnError = continueOnError, Name = stepName, Kind = PipelineStepKind.Clone, Tables = tables };
            }
            case "export": {
                CheckKeys(element, new[] { "name", "continueOnError", "export", "tables", "delimiter" }, where);
                string formatText = ReadString(value, $"{where}: \"export\"").Trim().ToLowerInvariant();
                ExportFormat format;
                switch (formatText) {
                    case "delta":
                        format = ExportFormat.Delta;
                        break;
                    case "csv":
                        format = ExportFormat.Csv;
                        break;
                    case "tsv":
                        format = ExportFormat.Tsv;
                        break;
                    case "delimited":
                        format = ExportFormat.Delimited;
                        break;
                    default:
                        throw new PipelineConfigException($"{where}: unknown export format '{formatText}'. Use delta, csv, tsv or delimited.");
                }

                char? delimiter = null;
                bool hasDelimiter = element.TryGetProperty("delimiter", out JsonElement delimiterElement);
                if (format == ExportFormat.Delimited) {
                    if (!hasDelimiter) {
                        throw new PipelineConfigException($"{where}: the delimited format needs a \"delimiter\" (one character, or \"tab\").");
                    }
                    string delimiterText = Substitute(ReadString(delimiterElement, $"{where}: \"delimiter\""), parameters, missing);
                    char parsed;
                    if (!PuddleShell.TryParseDelimiter(delimiterText, out parsed)) {
                        throw new PipelineConfigException($"{where}: \"delimiter\" must be a single character or \"tab\".");
                    }
                    try {
                        LocalStore.ValidateDelimiter(parsed);
                    } catch (ArgumentException ex) {
                        throw new PipelineConfigException($"{where}: {ex.Message}");
                    }
                    delimiter = parsed;
                } else if (hasDelimiter) {
                    throw new PipelineConfigException($"{where}: \"delimiter\" is only used with the delimited format.");
                }

                if (!element.TryGetProperty("tables", out JsonElement tablesElement)) {
                    throw new PipelineConfigException($"{where}: an export step needs \"tables\".");
                }
                List<string> tables = ReadTables(tablesElement, $"{where}: \"tables\"", parameters, missing);
                return new PipelineStep {
                    Index = index,
                    ContinueOnError = continueOnError,
                    Name = stepName,
                    Kind = PipelineStepKind.Export,
                    ExportFormat = format,
                    Delimiter = delimiter,
                    Tables = tables
                };
            }
            default: {
                CheckKeys(element, new[] { "name", "continueOnError", "assert", "expectRows" }, where);
                string query = Substitute(ReadString(value, $"{where}: \"assert\""), parameters, missing);
                if (SqlScriptSplitter.Split(query).Count != 1) {
                    throw new PipelineConfigException($"{where}: \"assert\" must be exactly one query.");
                }
                long expectRows = 0;
                if (element.TryGetProperty("expectRows", out JsonElement expectElement)) {
                    if (expectElement.ValueKind != JsonValueKind.Number || !expectElement.TryGetInt64(out expectRows) || expectRows < 0) {
                        throw new PipelineConfigException($"{where}: \"expectRows\" must be a whole number, 0 or more.");
                    }
                }
                return new PipelineStep {
                    Index = index,
                    ContinueOnError = continueOnError,
                    Name = stepName,
                    Kind = PipelineStepKind.Assert,
                    AssertQuery = query,
                    ExpectRows = expectRows
                };
            }
        }
    }

    // ---------- Helpers ----------

    private static void CheckKeys(JsonElement element, string[] allowed, string where) {
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!allowed.Contains(property.Name)) {
                throw new PipelineConfigException(
                    $"{where}: unknown key \"{property.Name}\". Allowed here: {string.Join(", ", allowed)}.");
            }
        }
    }

    private static string ReadString(JsonElement element, string where) {
        if (element.ValueKind != JsonValueKind.String) {
            throw new PipelineConfigException($"{where} must be a string.");
        }
        string? text = element.GetString();
        if (string.IsNullOrWhiteSpace(text)) {
            throw new PipelineConfigException($"{where} must not be empty.");
        }
        return text;
    }

    private static List<string> ReadStringOrList(JsonElement element, string where) {
        List<string> items = new List<string>();
        if (element.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in element.EnumerateArray()) {
                items.Add(ReadString(item, where));
            }
            if (items.Count == 0) {
                throw new PipelineConfigException($"{where} must not be an empty list.");
            }
        } else {
            items.Add(ReadString(element, where));
        }
        return items;
    }

    private static string ReadParameterValue(JsonElement element, string where) {
        switch (element.ValueKind) {
            case JsonValueKind.String:
                return element.GetString() ?? "";
            case JsonValueKind.Number:
                return element.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                throw new PipelineConfigException($"{where} must be a string, number or true/false.");
        }
    }

    // A table list may be one string ("a.b, c.d") or an array of strings.
    private static List<string> ReadTables(
        JsonElement element,
        string where,
        Dictionary<string, string> parameters,
        HashSet<string> missing) {

        List<string> raw = new List<string>();
        foreach (string text in ReadStringOrList(element, where)) {
            raw.Add(Substitute(text, parameters, missing));
        }
        try {
            List<(string Schema, string Table)> parsed = LocalStore.ParseTableList(raw);
            return parsed.Select(t => t.Schema + "." + t.Table).ToList();
        } catch (FormatException ex) {
            throw new PipelineConfigException($"{where}: {ex.Message}");
        }
    }

    // Replaces ${Name} with the parameter's value. Names with no value are recorded in 'missing'
    // and left in place; the caller reports them all together once every step has been read.
    private static string Substitute(string text, Dictionary<string, string> parameters, HashSet<string> missing) {
        return ParameterPattern.Replace(text, match => {
            string parameterName = match.Groups[1].Value;
            if (parameters.TryGetValue(parameterName, out string? parameterValue)) {
                return parameterValue;
            }
            missing.Add(parameterName);
            return match.Value;
        });
    }
}
