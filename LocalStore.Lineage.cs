using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;

namespace DataPuddle;

// Carries extended properties from source columns to the columns of tables created from them.
public sealed partial class LocalStore {
    private const string NamePart = @"(?:""(?:[^""]|"""")+""|\[[^\]]+\]|[\w$#@]+)";

    private static readonly Regex CreateTableAs = new Regex(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?TABLE\s+(?<name>" + NamePart + @"(?:\s*\.\s*" + NamePart + @"){0,2})\s+AS\s+(?<body>.+?)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex NamePartPattern = new Regex(NamePart, RegexOptions.Compiled);

    /// <summary>
    /// Call after running a statement. If it was CREATE TABLE ... AS SELECT (which is what SELECT ... INTO becomes), each
    /// new column that is a plain reference to a source column receives that source column's extended properties, and the
    /// MS_Description becomes its comment. Computed columns (functions, calculations, constants) are not tagged; a note
    /// lists those that read tagged columns. Other statements are ignored. A problem is reported through
    /// <see cref="Log"/> and never thrown.
    /// </summary>
    public void InheritExtendedProperties(string statement) {
        if (!_options.ExtendedProperties) {
            return;
        }
        Match match = CreateTableAs.Match(statement);
        if (!match.Success) {
            return;
        }

        try {
            if (!HasExtendedProperties()) {
                return;
            }

            (string schema, string table) = ParseTarget(match.Groups["name"].Value);
            List<string> targetColumns = ColumnNamesOf(schema, table);
            if (targetColumns.Count == 0) {
                return;
            }

            string json = ScalarString($"SELECT json_serialize_sql('{EscapeLiteral(match.Groups["body"].Value)}')");
            using (JsonDocument document = JsonDocument.Parse(json)) {
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.True) {
                    string message = root.TryGetProperty("error_message", out JsonElement text) ? text.GetString() ?? "" : "";
                    throw new LineageException("the query could not be analyzed: " + message);
                }

                JsonElement node = root.GetProperty("statements")[0].GetProperty("node");
                List<OutputColumn> outputs = new ColumnLineage(ColumnNamesOf).Analyze(node);
                if (outputs.Count != targetColumns.Count) {
                    throw new LineageException($"the query's columns could not be matched to the new table ({outputs.Count} found, {targetColumns.Count} created)");
                }
                ApplyInheritedProperties(schema, table, targetColumns, outputs);
            }
        } catch (Exception ex) {
            Log($"  extended properties were not inherited: {ex.Message}");
        }
    }

    private void ApplyInheritedProperties(string schema, string table, List<string> targetColumns, List<OutputColumn> outputs) {
        Dictionary<string, List<(string Name, string? Value)>> known = LoadColumnProperties();
        string meta = QuoteDuck(MetaSchema) + "." + QuoteDuck(MetaTable);

        // The new table replaces any earlier table of the same name, so its old rows go too.
        Execute($"DELETE FROM {meta} WHERE schema_name = '{EscapeLiteral(schema)}' AND table_name = '{EscapeLiteral(table)}'");

        int taggedColumns = 0;
        int propertyCount = 0;
        List<string> notTagged = new List<string>();
        for (int i = 0; i < outputs.Count; i++) {
            OutputColumn output = outputs[i];
            List<(string Name, string? Value)> inherited = new List<(string Name, string? Value)>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ColumnSource source in output.Sources) {
                if (known.TryGetValue(PropertyKey(source.Schema, source.Table, source.Column), out List<(string Name, string? Value)>? found)) {
                    foreach ((string Name, string? Value) property in found) {
                        if (seen.Add(property.Name + "\0" + property.Value)) {
                            inherited.Add(property);
                        }
                    }
                }
            }
            if (inherited.Count == 0) {
                continue;
            }

            string column = targetColumns[i];
            if (output.IsExpression) {
                notTagged.Add(column);
                continue;
            }

            foreach ((string Name, string? Value) property in inherited) {
                string value = property.Value == null ? "NULL" : "'" + EscapeLiteral(property.Value) + "'";
                Execute($"INSERT INTO {meta} VALUES ('{EscapeLiteral(schema)}', '{EscapeLiteral(table)}', '{EscapeLiteral(column)}', '{EscapeLiteral(property.Name)}', {value})");
                propertyCount++;
            }
            taggedColumns++;

            // One description across all sources becomes the column's comment; conflicting ones are left for the user to resolve.
            List<string> descriptions = inherited
                .Where(p => p.Name == DescriptionProperty && p.Value != null)
                .Select(p => p.Value!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (descriptions.Count == 1) {
                try {
                    Execute($"COMMENT ON COLUMN {QuoteDuck(schema)}.{QuoteDuck(table)}.{QuoteDuck(column)} IS '{EscapeLiteral(descriptions[0])}'");
                } catch (DuckDBException ex) {
                    Log($"  could not apply the description of {column}: {ex.Message}");
                }
            }
        }

        if (taggedColumns > 0) {
            Log($"  extended properties: {propertyCount:N0} inherited by {taggedColumns:N0} column(s) of {schema}.{table}");
        }
        if (notTagged.Count > 0) {
            Log($"  note: computed column(s) {string.Join(", ", notTagged)} read columns that have extended properties but were not tagged. " +
                $"Add rows to {MetaSchema}.{MetaTable} for them if they should be.");
        }
    }

    private static string PropertyKey(string schema, string table, string column) {
        return (schema + "\0" + table + "\0" + column).ToLowerInvariant();
    }

    private static (string Schema, string Table) ParseTarget(string name) {
        List<string> parts = new List<string>();
        foreach (Match part in NamePartPattern.Matches(name)) {
            string text = part.Value;
            if (text.StartsWith('"')) {
                text = text.Substring(1, text.Length - 2).Replace("\"\"", "\"");
            } else if (text.StartsWith('[')) {
                text = text.Substring(1, text.Length - 2);
            }
            parts.Add(text);
        }
        // A catalog name in front is ignored; with no schema the table is in main.
        if (parts.Count == 1) {
            return ("main", parts[0]);
        }
        return (parts[parts.Count - 2], parts[parts.Count - 1]);
    }

    private bool HasExtendedProperties() {
        long tables = ScalarLong(
            $"SELECT COUNT(*) FROM duckdb_tables() WHERE database_name = current_database() AND schema_name = '{MetaSchema}' AND table_name = '{MetaTable}'");
        if (tables == 0) {
            return false;
        }
        return ScalarLong($"SELECT COUNT(*) FROM {QuoteDuck(MetaSchema)}.{QuoteDuck(MetaTable)}") > 0;
    }

    private List<string> ColumnNamesOf(string schema, string table) {
        List<string> names = new List<string>();
        using (DuckDBCommand cmd = Connection.CreateCommand()) {
            cmd.CommandText =
                "SELECT column_name FROM duckdb_columns() WHERE database_name = current_database() " +
                $"AND lower(schema_name) = lower('{EscapeLiteral(schema)}') AND lower(table_name) = lower('{EscapeLiteral(table)}') ORDER BY column_index";
            using (DbDataReader reader = cmd.ExecuteReader()) {
                while (reader.Read()) {
                    names.Add(reader.GetString(0));
                }
            }
        }
        return names;
    }

    private Dictionary<string, List<(string Name, string? Value)>> LoadColumnProperties() {
        Dictionary<string, List<(string Name, string? Value)>> known = new Dictionary<string, List<(string Name, string? Value)>>();
        using (DuckDBCommand cmd = Connection.CreateCommand()) {
            cmd.CommandText =
                "SELECT schema_name, table_name, column_name, property_name, property_value " +
                $"FROM {QuoteDuck(MetaSchema)}.{QuoteDuck(MetaTable)} WHERE column_name IS NOT NULL";
            using (DbDataReader reader = cmd.ExecuteReader()) {
                while (reader.Read()) {
                    string key = PropertyKey(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                    if (!known.TryGetValue(key, out List<(string Name, string? Value)>? list)) {
                        list = new List<(string Name, string? Value)>();
                        known[key] = list;
                    }
                    list.Add((reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
            }
        }
        return known;
    }

    private string ScalarString(string sqlText) {
        using (DuckDBCommand cmd = Connection.CreateCommand()) {
            cmd.CommandText = sqlText;
            return Convert.ToString(cmd.ExecuteScalar()) ?? "";
        }
    }
}
