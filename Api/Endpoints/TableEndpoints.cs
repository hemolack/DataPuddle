using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DataPuddle.Api.Endpoints;

/// <summary>Endpoints that work with one table at a time: list, describe, read, insert, update, delete, import, download.</summary>
public static class TableEndpoints {
    private static readonly HashSet<string> ReadReserved =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "columns", "order", "limit", "offset", "format" };

    private static readonly HashSet<string> ChangeReserved =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "key", "upsert", "all" };

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/tables", ListTables)
            .WithTags("Tables")
            .WithSummary("List tables and views.")
            .WithDescription("Query: format=json|ndjson|csv.");

        app.MapGet("/tables/{schema}/{table}", DescribeTable)
            .WithTags("Tables")
            .WithSummary("Columns and row count of one table.");

        app.MapGet("/tables/{schema}/{table}/rows", GetRows)
            .WithTags("Rows")
            .WithSummary("Read rows.")
            .WithDescription(
                "Query: columns=a,b  order=a,-b  limit=n  offset=n  format=json|ndjson|csv. " +
                "Any other key filters on a column: ?status=open, ?status=open&status=held (either), " +
                "?amount.gt=100 (also .ne .gte .lt .lte .like .isnull).");

        app.MapPost("/tables/{schema}/{table}/rows", InsertRows)
            .WithTags("Rows")
            .WithSummary("Insert rows.")
            .WithDescription("Body: a JSON array of objects, one object, or newline-delimited JSON (Content-Type: application/x-ndjson). All rows are added or none are.");

        app.MapMethods("/tables/{schema}/{table}/rows", new[] { "PATCH" }, UpdateRows)
            .WithTags("Rows")
            .WithSummary("Update rows.")
            .WithDescription(
                "By key: ?key=id (or id,other) with a body of objects that each hold the key columns and the new values; add upsert=true to insert rows that are not found. " +
                "By filter: one body object of new values plus the same filters as reading rows (all=true to change every row).");

        app.MapDelete("/tables/{schema}/{table}/rows", DeleteRows)
            .WithTags("Rows")
            .WithSummary("Delete rows that match the filters.")
            .WithDescription("Needs at least one filter, or all=true to delete every row.");

        app.MapDelete("/tables/{schema}/{table}", DropTable)
            .WithTags("Tables")
            .WithSummary("Drop a table.");

        app.MapPost("/tables/{schema}/{table}/import", ImportFile)
            .WithTags("Import and export")
            .WithSummary("Load a whole file into a table.")
            .WithDescription(
                "Body: the file itself. Query: format=csv|parquet|json (default csv), mode=replace|append|create (default replace). " +
                "CSV options: delimiter, header=true|false, dateformat, timestampformat, nullstr, allVarchar=true.");

        app.MapGet("/tables/{schema}/{table}/download", Download)
            .WithTags("Import and export")
            .WithSummary("Download a whole table as a file.")
            .WithDescription("Query: format=csv|parquet (default csv).");
    }

    // ---------- Listing and describing ----------

    private static async Task ListTables(HttpContext context, ApiServices api) {
        api.Authorize(context, ApiAction.Read);
        ResultFormat format = ResultWriter.Negotiate(context);
        CancellationToken cancellationToken = context.RequestAborted;

        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Read, cancellationToken, context))
        using (DuckDBCommand command = session.CreateCommand(
            "SELECT schema_name AS \"schema\", table_name AS \"table\", 'table' AS kind, estimated_size AS estimated_rows " +
            "FROM duckdb_tables() WHERE database_name = current_database() " +
            "UNION ALL " +
            "SELECT schema_name, view_name, 'view', NULL FROM duckdb_views() " +
            "WHERE database_name = current_database() AND NOT internal " +
            "ORDER BY 1, 2"))
        using (session.StartTimer(command))
        using (DbDataReader reader = command.ExecuteReader()) {
            await ResultWriter.WriteAsync(context, reader, format, api.Options.MaxRows, cancellationToken);
        }
    }

    private static async Task DescribeTable(HttpContext context, ApiServices api, string schema, string table) {
        api.Authorize(context, ApiAction.Read, schema, table);
        CancellationToken cancellationToken = context.RequestAborted;

        List<ColumnInfo> columns;
        long rowCount;
        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Read, cancellationToken, context)) {
            columns = TableAccess.GetColumns(session, schema, table);
            using (DuckDBCommand command = session.CreateCommand("SELECT COUNT(*) FROM " + TableAccess.QualifiedName(schema, table)))
            using (session.StartTimer(command)) {
                rowCount = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        await context.Response.WriteAsJsonAsync(new {
            schema,
            table,
            rowCount,
            columns = columns.Select(c => new { name = c.Name, type = c.DataType, nullable = c.Nullable })
        }, cancellationToken);
    }

    // ---------- Reading ----------

    private static async Task GetRows(HttpContext context, ApiServices api, string schema, string table) {
        api.Authorize(context, ApiAction.Read, schema, table);
        CancellationToken cancellationToken = context.RequestAborted;
        ResultFormat format = ResultWriter.Negotiate(context);
        IQueryCollection query = context.Request.Query;

        int limit = ReadLimit(query["limit"].ToString(), api.Options.MaxRows);
        long offset = ReadOffset(query["offset"].ToString());

        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Read, cancellationToken, context)) {
            List<ColumnInfo> columns = TableAccess.GetColumns(session, schema, table);
            List<ColumnInfo> selected = TableAccess.ParseColumnList(query["columns"].ToString(), columns);
            string select = selected.Count == 0 ? "*" : string.Join(", ", selected.Select(c => TableAccess.Quote(c.Name)));
            WhereClause where = TableAccess.BuildWhere(query, columns, ReadReserved);
            string orderBy = TableAccess.BuildOrderBy(query["order"].ToString(), columns);

            // One extra row is requested so the writer can tell whether the result was cut short.
            string sql = $"SELECT {select} FROM {TableAccess.QualifiedName(schema, table)}{where.Sql}{orderBy} " +
                $"LIMIT {limit + 1} OFFSET {offset}";

            using (DuckDBCommand command = session.CreateCommand(sql, where.Parameters))
            using (session.StartTimer(command))
            using (DbDataReader reader = command.ExecuteReader()) {
                await ResultWriter.WriteAsync(context, reader, format, limit, cancellationToken);
            }
        }
    }

    private static int ReadLimit(string text, int maxRows) {
        if (string.IsNullOrWhiteSpace(text)) {
            return maxRows;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit) || limit < 1) {
            throw ApiException.BadRequest("limit must be a whole number, 1 or more.");
        }
        return Math.Min(limit, maxRows);
    }

    private static long ReadOffset(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return 0;
        }
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset) || offset < 0) {
            throw ApiException.BadRequest("offset must be a whole number, 0 or more.");
        }
        return offset;
    }

    // ---------- Writing ----------

    private static async Task InsertRows(HttpContext context, ApiServices api, string schema, string table) {
        api.Authorize(context, ApiAction.Write, schema, table);
        CancellationToken cancellationToken = context.RequestAborted;
        List<Dictionary<string, JsonElement>> rows = await RequestBody.ReadRowsAsync(context);

        long inserted = 0;
        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Write, cancellationToken, context)) {
            List<ColumnInfo> columns = TableAccess.GetColumns(session, schema, table);
            foreach (Dictionary<string, JsonElement> row in rows) {
                inserted += InsertRow(session, schema, table, columns, row);
            }
            session.Commit();
        }

        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new { inserted }, cancellationToken);
    }

    private static long InsertRow(DatabaseSession session, string schema, string table, List<ColumnInfo> columns, Dictionary<string, JsonElement> row) {
        List<ColumnInfo> used = row.Keys.Select(name => TableAccess.FindColumn(columns, name)).ToList();
        string target = TableAccess.QualifiedName(schema, table);
        string sql = used.Count == 0
            ? $"INSERT INTO {target} DEFAULT VALUES"
            : $"INSERT INTO {target} ({string.Join(", ", used.Select(c => TableAccess.Quote(c.Name)))}) " +
              $"VALUES ({string.Join(", ", used.Select(TableAccess.CastPlaceholder))})";
        List<object?> values = used.Select(c => TableAccess.ToParameter(row[c.Name])).ToList();

        using (DuckDBCommand command = session.CreateCommand(sql, values))
        using (session.StartTimer(command)) {
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }
    }

    private static async Task UpdateRows(HttpContext context, ApiServices api, string schema, string table) {
        api.Authorize(context, ApiAction.Write, schema, table);
        CancellationToken cancellationToken = context.RequestAborted;
        IQueryCollection query = context.Request.Query;
        string keyText = query["key"].ToString();
        bool upsert = string.Equals(query["upsert"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        List<Dictionary<string, JsonElement>> rows = await RequestBody.ReadRowsAsync(context);

        long updated = 0;
        long inserted = 0;
        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Write, cancellationToken, context)) {
            List<ColumnInfo> columns = TableAccess.GetColumns(session, schema, table);
            string target = TableAccess.QualifiedName(schema, table);

            if (!string.IsNullOrWhiteSpace(keyText)) {
                List<ColumnInfo> keyColumns = TableAccess.ParseColumnList(keyText, columns);
                foreach (Dictionary<string, JsonElement> row in rows) {
                    foreach (ColumnInfo keyColumn in keyColumns) {
                        if (!row.ContainsKey(keyColumn.Name)) {
                            throw ApiException.BadRequest($"Every row needs a value for the key column '{keyColumn.Name}'.");
                        }
                    }

                    List<ColumnInfo> changing = row.Keys
                        .Select(name => TableAccess.FindColumn(columns, name))
                        .Where(c => !keyColumns.Any(k => k.Name == c.Name))
                        .ToList();
                    if (changing.Count == 0) {
                        throw ApiException.BadRequest("Each row needs at least one column to change besides the key.");
                    }

                    string sql = $"UPDATE {target} SET {string.Join(", ", changing.Select(c => TableAccess.Quote(c.Name) + " = " + TableAccess.CastPlaceholder(c)))} " +
                        $"WHERE {string.Join(" AND ", keyColumns.Select(k => TableAccess.Quote(k.Name) + " = " + TableAccess.CastPlaceholder(k)))}";
                    List<object?> values = changing.Select(c => TableAccess.ToParameter(row[c.Name])).ToList();
                    values.AddRange(keyColumns.Select(k => TableAccess.ToParameter(row[k.Name])));

                    long affected;
                    using (DuckDBCommand command = session.CreateCommand(sql, values))
                    using (session.StartTimer(command)) {
                        affected = Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                    }

                    if (affected == 0 && upsert) {
                        inserted += InsertRow(session, schema, table, columns, row);
                    } else {
                        updated += affected;
                    }
                }
            } else {
                if (rows.Count != 1) {
                    throw ApiException.BadRequest("Without ?key=, send one object of new values and use filters to choose the rows.");
                }
                WhereClause where = TableAccess.BuildWhere(query, columns, ReadReserved.Union(ChangeReserved).ToHashSet(StringComparer.OrdinalIgnoreCase));
                RequireFilterOrAll(where, query);

                List<ColumnInfo> changing = rows[0].Keys.Select(name => TableAccess.FindColumn(columns, name)).ToList();
                if (changing.Count == 0) {
                    throw ApiException.BadRequest("The body needs at least one column to change.");
                }
                string sql = $"UPDATE {target} SET {string.Join(", ", changing.Select(c => TableAccess.Quote(c.Name) + " = " + TableAccess.CastPlaceholder(c)))}{where.Sql}";
                List<object?> values = changing.Select(c => TableAccess.ToParameter(rows[0][c.Name])).ToList();
                values.AddRange(where.Parameters);

                using (DuckDBCommand command = session.CreateCommand(sql, values))
                using (session.StartTimer(command)) {
                    updated = Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                }
            }
            session.Commit();
        }

        await context.Response.WriteAsJsonAsync(new { updated, inserted }, cancellationToken);
    }

    private static async Task DeleteRows(HttpContext context, ApiServices api, string schema, string table) {
        api.Authorize(context, ApiAction.Write, schema, table);
        CancellationToken cancellationToken = context.RequestAborted;
        IQueryCollection query = context.Request.Query;

        long deleted;
        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Write, cancellationToken, context)) {
            List<ColumnInfo> columns = TableAccess.GetColumns(session, schema, table);
            WhereClause where = TableAccess.BuildWhere(query, columns, ChangeReserved);
            RequireFilterOrAll(where, query);

            string sql = "DELETE FROM " + TableAccess.QualifiedName(schema, table) + where.Sql;
            using (DuckDBCommand command = session.CreateCommand(sql, where.Parameters))
            using (session.StartTimer(command)) {
                deleted = Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            }
            session.Commit();
        }

        await context.Response.WriteAsJsonAsync(new { deleted }, cancellationToken);
    }

    // A request that matches every row must say so on purpose.
    private static void RequireFilterOrAll(WhereClause where, IQueryCollection query) {
        bool all = string.Equals(query["all"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        if (where.IsEmpty && !all) {
            throw ApiException.BadRequest("This would change every row. Add a filter such as ?id=5, or all=true if you mean every row.");
        }
    }

    private static async Task DropTable(HttpContext context, ApiServices api, string schema, string table) {
        api.Authorize(context, ApiAction.Write, schema, table);
        CancellationToken cancellationToken = context.RequestAborted;

        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Write, cancellationToken, context)) {
            TableAccess.GetColumns(session, schema, table);
            session.Execute("DROP TABLE " + TableAccess.QualifiedName(schema, table));
            session.Commit();
        }

        await context.Response.WriteAsJsonAsync(new { dropped = schema + "." + table }, cancellationToken);
    }

    // ---------- Files ----------

    private static async Task ImportFile(HttpContext context, ApiServices api, string schema, string table) {
        api.Authorize(context, ApiAction.Write, schema, table);
        CancellationToken cancellationToken = context.RequestAborted;
        IQueryCollection query = context.Request.Query;

        string format = Choose(query["format"].ToString(), "csv", "csv", "parquet", "json");
        string mode = Choose(query["mode"].ToString(), "replace", "replace", "append", "create");
        string reader = BuildReader(format, query);

        string uploads = Path.Combine(api.OutputDirectory, "uploads");
        Directory.CreateDirectory(uploads);
        string file = Path.GetFullPath(Path.Combine(uploads, Guid.NewGuid().ToString("N") + "." + format));
        string target = TableAccess.QualifiedName(schema, table);

        try {
            using (FileStream stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                await context.Request.Body.CopyToAsync(stream, cancellationToken);
            }
            if (new FileInfo(file).Length == 0) {
                throw ApiException.BadRequest("The request body is empty. Send the file's bytes as the body.");
            }

            string source = reader.Replace("{file}", file.Replace('\\', '/').Replace("'", "''"));
            long rows;
            using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Write, cancellationToken, context)) {
                string sql;
                if (mode == "append") {
                    TableAccess.GetColumns(session, schema, table);
                    sql = $"INSERT INTO {target} BY NAME SELECT * FROM {source}";
                } else {
                    session.Execute("CREATE SCHEMA IF NOT EXISTS " + TableAccess.Quote(schema));
                    sql = (mode == "replace" ? "CREATE OR REPLACE TABLE " : "CREATE TABLE ") + $"{target} AS SELECT * FROM {source}";
                }

                using (DuckDBCommand command = session.CreateCommand(sql))
                using (session.StartTimer(command)) {
                    rows = Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                }
                session.Commit();
            }

            context.Response.StatusCode = mode == "append" ? StatusCodes.Status200OK : StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(new { table = schema + "." + table, mode, rows }, cancellationToken);
        } finally {
            try {
                File.Delete(file);
            } catch (IOException) {
                // A leftover upload is harmless and is removed with the output folder.
            }
        }
    }

    private static string Choose(string text, string defaultValue, params string[] allowed) {
        if (string.IsNullOrWhiteSpace(text)) {
            return defaultValue;
        }
        string value = text.Trim().ToLowerInvariant();
        if (!allowed.Contains(value)) {
            throw ApiException.BadRequest($"'{text}' is not one of: {string.Join(", ", allowed)}.");
        }
        return value;
    }

    // Returns the table-function call for the file, with {file} where the path goes.
    private static string BuildReader(string format, IQueryCollection query) {
        if (format == "parquet") {
            return "read_parquet('{file}')";
        }
        if (format == "json") {
            return "read_json_auto('{file}')";
        }

        List<string> options = new List<string>();
        string delimiter = query["delimiter"].ToString();
        if (!string.IsNullOrEmpty(delimiter)) {
            char character;
            if (delimiter.Equals("tab", StringComparison.OrdinalIgnoreCase) || delimiter == "\\t") {
                character = '\t';
            } else if (delimiter.Length == 1) {
                character = delimiter[0];
            } else {
                throw ApiException.BadRequest("delimiter must be a single character, or 'tab'.");
            }
            try {
                LocalStore.ValidateDelimiter(character);
            } catch (ArgumentException ex) {
                throw ApiException.BadRequest(ex.Message);
            }
            options.Add("delim = '" + (character == '\t' ? "\\t" : character.ToString().Replace("'", "''")) + "'");
        }

        string header = query["header"].ToString();
        if (!string.IsNullOrEmpty(header)) {
            if (!bool.TryParse(header, out bool hasHeader)) {
                throw ApiException.BadRequest("header must be true or false.");
            }
            options.Add("header = " + (hasHeader ? "true" : "false"));
        }
        AddTextOption(options, "dateformat", query["dateformat"].ToString());
        AddTextOption(options, "timestampformat", query["timestampformat"].ToString());
        AddTextOption(options, "nullstr", query["nullstr"].ToString());
        string allVarchar = query["allVarchar"].ToString();
        if (!string.IsNullOrEmpty(allVarchar)) {
            if (!bool.TryParse(allVarchar, out bool all)) {
                throw ApiException.BadRequest("allVarchar must be true or false.");
            }
            options.Add("all_varchar = " + (all ? "true" : "false"));
        }

        return "read_csv('{file}'" + (options.Count > 0 ? ", " + string.Join(", ", options) : "") + ")";
    }

    private static void AddTextOption(List<string> options, string name, string value) {
        if (!string.IsNullOrEmpty(value)) {
            options.Add(name + " = '" + value.Replace("'", "''") + "'");
        }
    }

    private static string SafeFileName(string name) {
        char[] bad = Path.GetInvalidFileNameChars().Concat(new[] { '"', ';', ',' }).ToArray();
        return new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray());
    }

    private static async Task Download(HttpContext context, ApiServices api, string schema, string table) {
        api.Authorize(context, ApiAction.Read, schema, table);
        CancellationToken cancellationToken = context.RequestAborted;
        string format = Choose(context.Request.Query["format"].ToString(), "csv", "csv", "parquet");

        string downloads = Path.Combine(api.OutputDirectory, "downloads");
        Directory.CreateDirectory(downloads);
        string file = Path.GetFullPath(Path.Combine(downloads, Guid.NewGuid().ToString("N") + "." + format));

        try {
            // The database is released as soon as the file is written, so a slow download never blocks other callers.
            using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Read, cancellationToken, context)) {
                TableAccess.GetColumns(session, schema, table);
                string options = format == "parquet" ? "FORMAT PARQUET" : "FORMAT CSV, HEADER true";
                string sql = $"COPY (SELECT * FROM {TableAccess.QualifiedName(schema, table)}) " +
                    $"TO '{file.Replace('\\', '/').Replace("'", "''")}' ({options})";
                using (DuckDBCommand command = session.CreateCommand(sql))
                using (session.StartTimer(command)) {
                    command.ExecuteNonQuery();
                }
            }

            context.Response.ContentType = format == "parquet" ? "application/vnd.apache.parquet" : "text/csv";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{SafeFileName(schema + "." + table)}.{format}\"";
            await context.Response.SendFileAsync(file, cancellationToken);
        } finally {
            try {
                File.Delete(file);
            } catch (IOException) {
                // A leftover download is harmless and is removed with the output folder.
            }
        }
    }
}
