using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DataPuddle.Api.Endpoints;

/// <summary>The escape hatch: run any SQL the caller sends.</summary>
public static class SqlEndpoints {
    // Statements that would end or reshape the transaction the server wraps around every request.
    private static readonly HashSet<string> TransactionKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BEGIN", "START", "COMMIT", "ROLLBACK", "ABORT", "END" };

    // Extra statements refused in read-only mode: they can write files or change settings without writing a table.
    private static readonly HashSet<string> ReadOnlyBlockedKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "COPY", "EXPORT", "IMPORT", "ATTACH", "DETACH", "INSTALL", "LOAD", "SET", "RESET", "PRAGMA", "CHECKPOINT", "VACUUM"
        };

    public static void Map(IEndpointRouteBuilder app) {
        app.MapPost("/sql", RunSql)
            .WithTags("SQL")
            .WithSummary("Run one SQL statement and return its rows.")
            .WithDescription(
                "Body: JSON {\"sql\": \"SELECT ... WHERE id = ?\", \"params\": [5], \"maxRows\": 100} or {\"params\": {\"id\": 5}} with $id in the SQL; or just the SQL as text/plain. " +
                "Query: format=json|ndjson|csv; autocommit=true runs the statement outside a transaction (for statements that need that). " +
                "Changes are committed when the statement succeeds. In read-only mode the statement runs in a read-only transaction.");

        app.MapPost("/script", RunScript)
            .WithTags("SQL")
            .WithSummary("Run several statements in one transaction.")
            .WithDescription("Body: JSON {\"sql\": \"...; ...\"} or the script as text/plain. If any statement fails, none of them take effect.");
    }

    private static async Task RunSql(HttpContext context, ApiServices api) {
        api.Authorize(context, ApiAction.Sql);
        CancellationToken cancellationToken = context.RequestAborted;
        ResultFormat format = ResultWriter.Negotiate(context);
        SqlRequest request = await SqlRequest.ReadAsync(context);

        ApiServer.NoteSql(context, request.Sql);
        CheckStatement(SingleStatement(request.Sql), api.Options.ReadOnly);

        bool autocommit = string.Equals(context.Request.Query["autocommit"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        AccessMode mode;
        if (api.Options.ReadOnly) {
            mode = AccessMode.Read;
        } else {
            mode = autocommit ? AccessMode.Exclusive : AccessMode.Write;
        }

        int maxRows = request.MaxRows.HasValue ? Math.Min(request.MaxRows.Value, api.Options.MaxRows) : api.Options.MaxRows;

        using (DatabaseSession session = await api.Gate.EnterAsync(mode, cancellationToken, context))
        using (DuckDBCommand command = session.CreateCommand(request.Sql, request.Positional, request.Named))
        using (session.StartTimer(command))
        using (DbDataReader reader = command.ExecuteReader()) {
            await ResultWriter.WriteAsync(context, reader, format, maxRows, cancellationToken);
            session.Commit();
        }
    }

    private static async Task RunScript(HttpContext context, ApiServices api) {
        api.Authorize(context, ApiAction.Write);
        CancellationToken cancellationToken = context.RequestAborted;
        SqlRequest request = await SqlRequest.ReadAsync(context);
        ApiServer.NoteSql(context, request.Sql);

        List<string> statements = SqlScriptSplitter.Split(request.Sql);
        if (statements.Count == 0) {
            throw ApiException.BadRequest("The script holds no statements.");
        }
        foreach (string statement in statements) {
            CheckStatement(statement, api.Options.ReadOnly);
        }

        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Write, cancellationToken, context)) {
            foreach (string statement in statements) {
                using (DuckDBCommand command = session.CreateCommand(statement))
                using (session.StartTimer(command)) {
                    command.ExecuteNonQuery();
                }
            }
            session.Commit();
        }

        await context.Response.WriteAsJsonAsync(new { statements = statements.Count }, cancellationToken);
    }

    /// <summary>Splits the SQL and requires exactly one statement.</summary>
    internal static string SingleStatement(string sql) {
        List<string> statements = SqlScriptSplitter.Split(sql);
        if (statements.Count == 0) {
            throw ApiException.BadRequest("The request holds no SQL.");
        }
        if (statements.Count > 1) {
            throw ApiException.BadRequest("Send one statement per request, or use /script to run several in one transaction.");
        }
        return statements[0];
    }

    /// <summary>
    /// Refuses statements that manage transactions, and in read-only mode statements that write files or
    /// change settings. This gives callers a clear message; what actually keeps read-only mode read-only
    /// is the read-only transaction the database enforces.
    /// </summary>
    internal static void CheckStatement(string statement, bool readOnly) {
        string keyword = FirstKeyword(statement);
        if (TransactionKeywords.Contains(keyword)) {
            throw ApiException.BadRequest("Do not send BEGIN, COMMIT or ROLLBACK. Each request already runs in its own transaction; use /script for several statements in one.");
        }
        if (readOnly && ReadOnlyBlockedKeywords.Contains(keyword)) {
            throw ApiException.Forbidden($"{keyword.ToUpperInvariant()} is not allowed in read-only mode.");
        }
    }

    private static string FirstKeyword(string statement) {
        int i = 0;
        while (i < statement.Length) {
            if (char.IsWhiteSpace(statement[i])) {
                i++;
            } else if (statement[i] == '-' && i + 1 < statement.Length && statement[i + 1] == '-') {
                int end = statement.IndexOf('\n', i);
                i = end < 0 ? statement.Length : end + 1;
            } else if (statement[i] == '/' && i + 1 < statement.Length && statement[i + 1] == '*') {
                int close = statement.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? statement.Length : close + 2;
            } else {
                break;
            }
        }

        int start = i;
        while (i < statement.Length && (char.IsLetter(statement[i]) || statement[i] == '_')) {
            i++;
        }
        return statement.Substring(start, i - start);
    }

    /// <summary>The SQL, its parameters and an optional row limit, from either body style.</summary>
    private sealed class SqlRequest {
        public string Sql { get; private init; } = "";
        public List<object?>? Positional { get; private init; }
        public Dictionary<string, object?>? Named { get; private init; }
        public int? MaxRows { get; private init; }

        public static async Task<SqlRequest> ReadAsync(HttpContext context) {
            string contentType = context.Request.ContentType ?? "";
            if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) {
                string text = await RequestBody.ReadTextAsync(context);
                if (string.IsNullOrWhiteSpace(text)) {
                    throw ApiException.BadRequest("The request needs SQL in the body.");
                }
                return new SqlRequest { Sql = text };
            }

            using (JsonDocument document = await RequestBody.ReadJsonAsync(context)) {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("sql", out JsonElement sqlElement) ||
                    sqlElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(sqlElement.GetString())) {
                    throw ApiException.BadRequest("The JSON body needs a \"sql\" string, for example {\"sql\": \"SELECT 1\"}.");
                }

                List<object?>? positional = null;
                Dictionary<string, object?>? named = null;
                if (root.TryGetProperty("params", out JsonElement parameters)) {
                    if (parameters.ValueKind == JsonValueKind.Array) {
                        positional = parameters.EnumerateArray().Select(ToValue).ToList();
                    } else if (parameters.ValueKind == JsonValueKind.Object) {
                        named = new Dictionary<string, object?>();
                        foreach (JsonProperty property in parameters.EnumerateObject()) {
                            named[property.Name] = ToValue(property.Value);
                        }
                    } else if (parameters.ValueKind != JsonValueKind.Null) {
                        throw ApiException.BadRequest("\"params\" must be an array (for ?) or an object (for $name).");
                    }
                }

                int? maxRows = null;
                if (root.TryGetProperty("maxRows", out JsonElement maxRowsElement) && maxRowsElement.ValueKind != JsonValueKind.Null) {
                    if (maxRowsElement.ValueKind != JsonValueKind.Number || !maxRowsElement.TryGetInt32(out int value) || value < 1) {
                        throw ApiException.BadRequest("\"maxRows\" must be a whole number, 1 or more.");
                    }
                    maxRows = value;
                }

                return new SqlRequest {
                    Sql = sqlElement.GetString()!,
                    Positional = positional,
                    Named = named,
                    MaxRows = maxRows
                };
            }
        }

        private static object? ToValue(JsonElement element) {
            switch (element.ValueKind) {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out long whole)) {
                        return whole;
                    }
                    if (element.TryGetDecimal(out decimal exact)) {
                        return exact;
                    }
                    return element.GetDouble();
                default:
                    return element.GetRawText();
            }
        }
    }
}
