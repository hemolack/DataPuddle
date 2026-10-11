using System.Text.Json;

namespace DataPuddle;

/// <summary>A column of a source table that an output column is taken from.</summary>
internal sealed record ColumnSource(string Schema, string Table, string Column);

/// <summary>One column a query produces, and where it comes from.</summary>
internal sealed class OutputColumn {
    /// <summary>The name the query gives it. Empty when it has none (an unnamed expression).</summary>
    public string Name { get; init; } = "";

    /// <summary>The source columns it reads. A plain column reference has one (or several for a UNION).</summary>
    public List<ColumnSource> Sources { get; init; } = new List<ColumnSource>();

    /// <summary>True when the value is computed (a function, calculation, constant...) rather than a plain column.</summary>
    public bool IsExpression { get; init; }

    public OutputColumn Copy() {
        return new OutputColumn { Name = Name, Sources = new List<ColumnSource>(Sources), IsExpression = IsExpression };
    }
}

/// <summary>The query's text could not be traced to its source columns.</summary>
internal sealed class LineageException : Exception {
    public LineageException(string message) : base(message) {
    }
}

/// <summary>
/// Works out, for each column a SELECT produces, which table columns it comes from. It reads the query's parse tree
/// (from json_serialize_sql), so aliases, joins, sub-queries, WITH clauses, UNIONs and * are all followed.
/// </summary>
internal sealed class ColumnLineage {
    private sealed class Relation {
        public string Alias { get; init; } = "";
        public List<OutputColumn> Columns { get; init; } = new List<OutputColumn>();

        /// <summary>True for something whose columns are unknown (such as a table function).</summary>
        public bool Opaque { get; init; }

        /// <summary>Columns left out of an unqualified * because a USING join already supplies them.</summary>
        public HashSet<string> StarSkip { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Func<string, string, List<string>> _tableColumns;

    /// <param name="tableColumns">Returns the column names of a schema and table, in order, or an empty list if it does not exist.</param>
    public ColumnLineage(Func<string, string, List<string>> tableColumns) {
        _tableColumns = tableColumns;
    }

    public List<OutputColumn> Analyze(JsonElement node) {
        return AnalyzeNode(node, new Dictionary<string, List<OutputColumn>>(StringComparer.OrdinalIgnoreCase));
    }

    private List<OutputColumn> AnalyzeNode(JsonElement node, Dictionary<string, List<OutputColumn>> inheritedCtes) {
        string type = Text(node, "type");
        Dictionary<string, List<OutputColumn>> ctes = new Dictionary<string, List<OutputColumn>>(inheritedCtes, StringComparer.OrdinalIgnoreCase);
        if (node.TryGetProperty("cte_map", out JsonElement cteMap) && cteMap.TryGetProperty("map", out JsonElement entries)) {
            foreach (JsonElement entry in entries.EnumerateArray()) {
                JsonElement query = entry.GetProperty("value").GetProperty("query").GetProperty("node");
                ctes[entry.GetProperty("key").GetString() ?? ""] = AnalyzeNode(query, ctes);
            }
        }

        if (type == "SET_OPERATION_NODE") {
            List<OutputColumn> left = AnalyzeNode(node.GetProperty("left"), ctes);
            List<OutputColumn> right = AnalyzeNode(node.GetProperty("right"), ctes);
            if (left.Count != right.Count) {
                throw new LineageException("the parts of a UNION have different column counts");
            }
            List<OutputColumn> merged = new List<OutputColumn>();
            for (int i = 0; i < left.Count; i++) {
                OutputColumn column = left[i].Copy();
                column.Sources.AddRange(right[i].Sources);
                merged.Add(new OutputColumn { Name = column.Name, Sources = column.Sources, IsExpression = left[i].IsExpression || right[i].IsExpression });
            }
            return merged;
        }
        if (type != "SELECT_NODE") {
            throw new LineageException("the query is not a plain SELECT (" + type + ")");
        }

        List<Relation> relations = new List<Relation>();
        if (node.TryGetProperty("from_table", out JsonElement from) && from.ValueKind == JsonValueKind.Object) {
            AddRelations(from, relations, ctes);
        }

        List<OutputColumn> outputs = new List<OutputColumn>();
        foreach (JsonElement item in node.GetProperty("select_list").EnumerateArray()) {
            string kind = Text(item, "class");
            string alias = Text(item, "alias");
            if (kind == "STAR") {
                outputs.AddRange(ExpandStar(item, relations));
            } else if (kind == "COLUMN_REF") {
                List<string> names = ColumnNames(item);
                outputs.Add(new OutputColumn {
                    Name = alias.Length > 0 ? alias : names[names.Count - 1],
                    Sources = Resolve(names, relations),
                    IsExpression = false
                });
            } else {
                List<ColumnSource> sources = new List<ColumnSource>();
                CollectColumnRefs(item, relations, sources);
                outputs.Add(new OutputColumn { Name = alias, Sources = sources, IsExpression = true });
            }
        }
        return outputs;
    }

    private void AddRelations(JsonElement table, List<Relation> relations, Dictionary<string, List<OutputColumn>> ctes) {
        string type = Text(table, "type");
        string alias = Text(table, "alias");
        switch (type) {
            case "JOIN": {
                AddRelations(table.GetProperty("left"), relations, ctes);
                int rightStart = relations.Count;
                AddRelations(table.GetProperty("right"), relations, ctes);
                if (table.TryGetProperty("using_columns", out JsonElement usingColumns)) {
                    foreach (JsonElement name in usingColumns.EnumerateArray()) {
                        for (int i = rightStart; i < relations.Count; i++) {
                            relations[i].StarSkip.Add(name.GetString() ?? "");
                        }
                    }
                }
                break;
            }
            case "BASE_TABLE": {
                string schema = Text(table, "schema_name");
                string name = Text(table, "table_name");
                if (ArrayLength(table, "column_name_alias") > 0) {
                    throw new LineageException("a table with renamed columns (AS t(a, b)) is not supported");
                }
                if (schema.Length == 0 && ctes.TryGetValue(name, out List<OutputColumn>? cte)) {
                    relations.Add(new Relation { Alias = alias.Length > 0 ? alias : name, Columns = cte.Select(c => c.Copy()).ToList() });
                } else {
                    string resolvedSchema = schema.Length == 0 ? "main" : schema;
                    List<string> columnNames = _tableColumns(resolvedSchema, name);
                    relations.Add(new Relation {
                        Alias = alias.Length > 0 ? alias : name,
                        Columns = columnNames.Select(c => new OutputColumn {
                            Name = c,
                            Sources = new List<ColumnSource> { new ColumnSource(resolvedSchema, name, c) }
                        }).ToList(),
                        Opaque = columnNames.Count == 0
                    });
                }
                break;
            }
            case "SUBQUERY":
                relations.Add(new Relation {
                    Alias = alias,
                    Columns = AnalyzeNode(table.GetProperty("subquery").GetProperty("node"), ctes)
                });
                break;
            default:
                relations.Add(new Relation { Alias = alias, Opaque = true });
                break;
        }
    }

    private static List<OutputColumn> ExpandStar(JsonElement star, List<Relation> relations) {
        if (BoolProperty(star, "columns") || (star.TryGetProperty("expr", out JsonElement expr) && expr.ValueKind == JsonValueKind.Object) ||
            ArrayLength(star, "replace_list") > 0 || ArrayLength(star, "rename_list") > 0 || ArrayLength(star, "qualified_exclude_list") > 0) {
            throw new LineageException("this form of * (COLUMNS, REPLACE, RENAME) is not supported");
        }

        HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (star.TryGetProperty("exclude_list", out JsonElement excludeList)) {
            foreach (JsonElement name in excludeList.EnumerateArray()) {
                excluded.Add(name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "");
            }
        }

        string qualifier = Text(star, "relation_name");
        List<OutputColumn> columns = new List<OutputColumn>();
        bool matched = false;
        foreach (Relation relation in relations) {
            if (qualifier.Length > 0 && !string.Equals(relation.Alias, qualifier, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            matched = true;
            if (relation.Opaque) {
                throw new LineageException("* over a source whose columns are not known");
            }
            foreach (OutputColumn column in relation.Columns) {
                if (excluded.Contains(column.Name) || (qualifier.Length == 0 && relation.StarSkip.Contains(column.Name))) {
                    continue;
                }
                columns.Add(column.Copy());
            }
        }
        if (!matched) {
            throw new LineageException("* refers to an unknown table");
        }
        return columns;
    }

    // Finds the source columns of a column reference such as a, t.a or schema.t.a.
    private static List<ColumnSource> Resolve(List<string> names, List<Relation> relations) {
        List<ColumnSource> sources = new List<ColumnSource>();
        string column = names[names.Count - 1];
        string? qualifier = names.Count >= 2 ? names[names.Count - 2] : null;
        foreach (Relation relation in relations) {
            if (qualifier != null && !string.Equals(relation.Alias, qualifier, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            foreach (OutputColumn candidate in relation.Columns) {
                if (string.Equals(candidate.Name, column, StringComparison.OrdinalIgnoreCase)) {
                    sources.AddRange(candidate.Sources);
                }
            }
        }
        return sources;
    }

    // Every column reference anywhere inside an expression.
    private static void CollectColumnRefs(JsonElement element, List<Relation> relations, List<ColumnSource> sources) {
        if (element.ValueKind == JsonValueKind.Object) {
            if (Text(element, "class") == "COLUMN_REF") {
                sources.AddRange(Resolve(ColumnNames(element), relations));
                return;
            }
            foreach (JsonProperty property in element.EnumerateObject()) {
                CollectColumnRefs(property.Value, relations, sources);
            }
        } else if (element.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement child in element.EnumerateArray()) {
                CollectColumnRefs(child, relations, sources);
            }
        }
    }

    private static List<string> ColumnNames(JsonElement columnRef) {
        List<string> names = new List<string>();
        foreach (JsonElement name in columnRef.GetProperty("column_names").EnumerateArray()) {
            names.Add(name.GetString() ?? "");
        }
        return names;
    }

    private static string Text(JsonElement element, string property) {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String) {
            return value.GetString() ?? "";
        }
        return "";
    }

    private static bool BoolProperty(JsonElement element, string property) {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }

    private static int ArrayLength(JsonElement element, string property) {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
    }
}
