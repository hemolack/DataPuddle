using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace DataPuddle.Api;

public sealed record ColumnInfo(string Name, string DataType, bool Nullable, int Position);

/// <summary>A WHERE clause (empty, or starting with " WHERE ") and the values for its ? placeholders.</summary>
public sealed record WhereClause(string Sql, List<object?> Parameters) {
    public bool IsEmpty {
        get {
            return Sql.Length == 0;
        }
    }
}

/// <summary>Helpers shared by the table endpoints: names, columns, filters and value conversion.</summary>
public static class TableAccess {
    private static readonly string[] Operators = { "eq", "ne", "gt", "gte", "lt", "lte", "like", "isnull" };

    public static string Quote(string identifier) {
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public static string QualifiedName(string schema, string table) {
        return Quote(schema) + "." + Quote(table);
    }

    /// <summary>The table's columns in order. Throws 404 when the table (or view) does not exist.</summary>
    public static List<ColumnInfo> GetColumns(DatabaseSession session, string schema, string table) {
        List<ColumnInfo> columns = new List<ColumnInfo>();
        using (DuckDBCommand command = session.CreateCommand(
            "SELECT column_name, data_type, is_nullable, column_index FROM duckdb_columns() " +
            "WHERE database_name = current_database() AND schema_name = ? AND table_name = ? ORDER BY column_index",
            new object?[] { schema, table }))
        using (DbDataReader reader = command.ExecuteReader()) {
            while (reader.Read()) {
                columns.Add(new ColumnInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture)));
            }
        }

        if (columns.Count == 0) {
            throw ApiException.NotFound($"There is no table or view named {schema}.{table}.");
        }
        return columns;
    }

    public static ColumnInfo FindColumn(List<ColumnInfo> columns, string name) {
        ColumnInfo? match = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match == null) {
            throw ApiException.BadRequest($"There is no column named '{name}'. Columns: {string.Join(", ", columns.Select(c => c.Name))}.");
        }
        return match;
    }

    /// <summary>The expression that turns a bound value into the column's type.</summary>
    public static string CastPlaceholder(ColumnInfo column) {
        return "CAST(? AS " + column.DataType + ")";
    }

    /// <summary>
    /// Builds a WHERE clause from query-string filters. ?status=open means status = 'open'; repeating a
    /// key means any of the values; a suffix picks another test: .ne .gt .gte .lt .lte .like .isnull
    /// (for example ?amount.gt=100&amp;deleted.isnull=true). Every value is bound as a parameter and cast to
    /// the column's type, so nothing the caller sends becomes SQL text. Names in <paramref name="reserved"/>
    /// belong to the endpoint and are skipped.
    /// </summary>
    public static WhereClause BuildWhere(IQueryCollection query, List<ColumnInfo> columns, ISet<string> reserved) {
        List<string> conditions = new List<string>();
        List<object?> parameters = new List<object?>();

        foreach (KeyValuePair<string, StringValues> pair in query) {
            if (reserved.Contains(pair.Key)) {
                continue;
            }

            string columnName = pair.Key;
            string op = "eq";
            ColumnInfo? column = columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (column == null) {
                int dot = columnName.LastIndexOf('.');
                if (dot > 0) {
                    string suffix = columnName.Substring(dot + 1).ToLowerInvariant();
                    if (Array.IndexOf(Operators, suffix) >= 0) {
                        op = suffix;
                        columnName = columnName.Substring(0, dot);
                    }
                }
                column = FindColumn(columns, columnName);
            }

            string quoted = Quote(column.Name);
            string?[] values = pair.Value.ToArray();

            if (op == "isnull") {
                bool wantNull = !string.Equals(values[0], "false", StringComparison.OrdinalIgnoreCase);
                conditions.Add(quoted + (wantNull ? " IS NULL" : " IS NOT NULL"));
            } else if (op == "like") {
                List<string> parts = new List<string>();
                foreach (string? value in values) {
                    parts.Add(quoted + " ILIKE ?");
                    parameters.Add(value ?? "");
                }
                conditions.Add(parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")");
            } else if (op == "eq") {
                if (values.Length == 1) {
                    conditions.Add(quoted + " = " + CastPlaceholder(column));
                    parameters.Add(values[0]);
                } else {
                    conditions.Add(quoted + " IN (" + string.Join(", ", values.Select(_ => CastPlaceholder(column))) + ")");
                    parameters.AddRange(values);
                }
            } else {
                string symbol = op switch {
                    "ne" => "<>",
                    "gt" => ">",
                    "gte" => ">=",
                    "lt" => "<",
                    _ => "<="
                };
                if (values.Length != 1) {
                    throw ApiException.BadRequest($"{pair.Key} takes a single value.");
                }
                conditions.Add(quoted + " " + symbol + " " + CastPlaceholder(column));
                parameters.Add(values[0]);
            }
        }

        if (conditions.Count == 0) {
            return new WhereClause("", parameters);
        }
        return new WhereClause(" WHERE " + string.Join(" AND ", conditions), parameters);
    }

    /// <summary>
    /// Turns a JSON value into the text bound to a CAST(? AS type) placeholder. Strings, numbers and
    /// booleans become their plain text, null stays null, and arrays or objects are passed as their JSON text.
    /// </summary>
    public static object? ToParameter(JsonElement value) {
        switch (value.ValueKind) {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                return value.GetRawText();
        }
    }

    /// <summary>Parses a comma-separated list of column names, checking each exists.</summary>
    public static List<ColumnInfo> ParseColumnList(string? text, List<ColumnInfo> columns) {
        List<ColumnInfo> result = new List<ColumnInfo>();
        if (string.IsNullOrWhiteSpace(text)) {
            return result;
        }
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            result.Add(FindColumn(columns, part));
        }
        return result;
    }

    /// <summary>ORDER BY from ?order=name,-created (a leading minus means descending).</summary>
    public static string BuildOrderBy(string? order, List<ColumnInfo> columns) {
        if (string.IsNullOrWhiteSpace(order)) {
            return "";
        }
        List<string> parts = new List<string>();
        foreach (string item in order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            bool descending = item.StartsWith('-');
            string name = item.TrimStart('-', '+');
            ColumnInfo column = FindColumn(columns, name);
            parts.Add(Quote(column.Name) + (descending ? " DESC" : " ASC"));
        }
        return parts.Count == 0 ? "" : " ORDER BY " + string.Join(", ", parts);
    }

    /// <summary>Names a path segment can safely be used as a schema or table name (rejects empty names).</summary>
    public static void CheckName(string name, string what) {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) {
            throw ApiException.BadRequest($"The {what} name is not valid.");
        }
    }
}
