using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace DataPuddle.Api;

public enum QueryMode {
    /// <summary>The query only reads. It always runs in a read-only transaction.</summary>
    Read,

    /// <summary>The query may change data. It needs write access and is called with POST.</summary>
    Write
}

public enum QueryParameterType {
    String,
    Integer,
    Number,
    Boolean
}

public sealed class NamedQueryParameter {
    public string Name { get; init; } = "";
    public QueryParameterType Type { get; init; } = QueryParameterType.String;
    public bool Required { get; init; }
    public string? Default { get; init; }
}

/// <summary>
/// A query defined in the config file and called by name, so callers never send SQL. The SQL refers to its
/// parameters as $name; callers supply them as query-string values or in a JSON body.
/// </summary>
public sealed class NamedQuery {
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Sql { get; init; } = "";
    public QueryMode Mode { get; init; } = QueryMode.Read;
    public int? MaxRows { get; init; }
    public IReadOnlyList<NamedQueryParameter> Parameters { get; init; } = new List<NamedQueryParameter>();

    /// <summary>
    /// Checks the values a caller supplied against the declared parameters and returns the values to bind,
    /// converted to their declared types. Throws a 400 for a missing, unknown or malformed value.
    /// </summary>
    public Dictionary<string, object?> Bind(IReadOnlyDictionary<string, string> supplied) {
        foreach (string key in supplied.Keys) {
            if (!Parameters.Any(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))) {
                string expected = Parameters.Count == 0 ? "none" : string.Join(", ", Parameters.Select(p => p.Name));
                throw ApiException.BadRequest($"The query '{Name}' has no parameter '{key}'. Parameters: {expected}.");
            }
        }

        Dictionary<string, object?> values = new Dictionary<string, object?>();
        foreach (NamedQueryParameter parameter in Parameters) {
            string? text = null;
            foreach (KeyValuePair<string, string> pair in supplied) {
                if (string.Equals(pair.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)) {
                    text = pair.Value;
                }
            }
            text ??= parameter.Default;

            if (text == null) {
                if (parameter.Required) {
                    throw ApiException.BadRequest($"The query '{Name}' needs the parameter '{parameter.Name}'.");
                }
                values[parameter.Name] = null;
                continue;
            }
            values[parameter.Name] = Convert(parameter, text);
        }
        return values;
    }

    private static object Convert(NamedQueryParameter parameter, string text) {
        switch (parameter.Type) {
            case QueryParameterType.Integer:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole)) {
                    return whole;
                }
                throw ApiException.BadRequest($"The parameter '{parameter.Name}' must be a whole number.");
            case QueryParameterType.Number:
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)) {
                    return number;
                }
                throw ApiException.BadRequest($"The parameter '{parameter.Name}' must be a number.");
            case QueryParameterType.Boolean:
                if (bool.TryParse(text, out bool flag)) {
                    return flag;
                }
                throw ApiException.BadRequest($"The parameter '{parameter.Name}' must be true or false.");
            default:
                return text;
        }
    }
}

/// <summary>Reads the Api:Queries section of the config file.</summary>
public static class NamedQueryLoader {
    private static readonly Regex NamePattern = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex ParameterPattern = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    public static Dictionary<string, NamedQuery> Load(IConfigurationSection section, string configDirectory) {
        Dictionary<string, NamedQuery> queries = new Dictionary<string, NamedQuery>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection child in section.GetChildren()) {
            string label = "Api:Queries:" + child.Key;
            if (!NamePattern.IsMatch(child.Key)) {
                throw new ApiConfigException($"{label}: a query name may only use letters, digits, '-' and '_'.");
            }
            queries[child.Key] = LoadOne(child, label, configDirectory);
        }
        return queries;
    }

    private static NamedQuery LoadOne(IConfigurationSection section, string label, string configDirectory) {
        string? sql = section["Sql"];
        string? sqlFile = section["SqlFile"];
        if (!string.IsNullOrWhiteSpace(sql) == !string.IsNullOrWhiteSpace(sqlFile)) {
            throw new ApiConfigException($"{label}: set exactly one of \"Sql\" and \"SqlFile\".");
        }
        if (!string.IsNullOrWhiteSpace(sqlFile)) {
            string path = Path.IsPathRooted(sqlFile) ? sqlFile : Path.Combine(configDirectory, sqlFile);
            if (!File.Exists(path)) {
                throw new ApiConfigException($"{label}: the SQL file was not found: {path}");
            }
            sql = File.ReadAllText(path);
        }

        string statement;
        try {
            statement = Endpoints.SqlEndpoints.SingleStatement(sql!);
            Endpoints.SqlEndpoints.CheckStatement(statement, false);
        } catch (ApiException ex) {
            throw new ApiConfigException($"{label}: {ex.Message}");
        }

        QueryMode mode = QueryMode.Read;
        string? modeText = section["Mode"];
        if (!string.IsNullOrWhiteSpace(modeText) && !Enum.TryParse(modeText, true, out mode)) {
            throw new ApiConfigException($"{label}: \"Mode\" must be read or write.");
        }

        int? maxRows = null;
        string? maxRowsText = section["MaxRows"];
        if (!string.IsNullOrWhiteSpace(maxRowsText)) {
            if (!int.TryParse(maxRowsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 1) {
                throw new ApiConfigException($"{label}: \"MaxRows\" must be a whole number, 1 or more.");
            }
            maxRows = parsed;
        }

        List<NamedQueryParameter> parameters = new List<NamedQueryParameter>();
        foreach (IConfigurationSection item in section.GetSection("Parameters").GetChildren()) {
            string? name = item["Name"];
            if (string.IsNullOrWhiteSpace(name) || !ParameterPattern.IsMatch(name)) {
                throw new ApiConfigException($"{label}: every parameter needs a \"Name\" of letters, digits and '_' that does not start with a digit.");
            }
            if (parameters.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) {
                throw new ApiConfigException($"{label}: the parameter '{name}' is listed twice.");
            }

            QueryParameterType type = QueryParameterType.String;
            string? typeText = item["Type"];
            if (!string.IsNullOrWhiteSpace(typeText) && !Enum.TryParse(typeText, true, out type)) {
                throw new ApiConfigException($"{label}: the parameter '{name}' has Type '{typeText}'; use string, integer, number or boolean.");
            }

            string? defaultValue = item["Default"];
            bool required = defaultValue == null;
            string? requiredText = item["Required"];
            if (!string.IsNullOrWhiteSpace(requiredText)) {
                if (!bool.TryParse(requiredText, out required)) {
                    throw new ApiConfigException($"{label}: the parameter '{name}' has Required that is not true or false.");
                }
            }
            parameters.Add(new NamedQueryParameter { Name = name, Type = type, Required = required, Default = defaultValue });
        }

        return new NamedQuery {
            Name = section.Key,
            Description = section["Description"] ?? "",
            Sql = statement,
            Mode = mode,
            MaxRows = maxRows,
            Parameters = parameters
        };
    }
}
