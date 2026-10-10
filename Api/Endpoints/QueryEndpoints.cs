using System.Data.Common;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DataPuddle.Api.Endpoints;

/// <summary>Queries defined in the config file and called by name, so callers never send SQL.</summary>
public static class QueryEndpoints {
    private static readonly HashSet<string> Reserved =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "format", "maxRows" };

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/queries", ListQueries)
            .WithTags("Named queries")
            .WithSummary("List the queries callers can run, with their parameters.");

        app.MapGet("/queries/{name}", RunQuery)
            .WithTags("Named queries")
            .WithSummary("Run a named query.")
            .WithDescription("Parameters are query-string values: /queries/open-invoices?customerId=1234. Also format=json|ndjson|csv|arrow and maxRows. Queries that change data must use POST.");

        app.MapPost("/queries/{name}", RunQuery)
            .WithTags("Named queries")
            .WithSummary("Run a named query, with parameters in a JSON body.")
            .WithDescription("Body (optional): {\"customerId\": 1234}. Query-string values work too; the body wins when both give the same parameter.");
    }

    private static async Task ListQueries(HttpContext context, ApiServices api) {
        api.Authorize(context, ApiAction.Query);
        await context.Response.WriteAsJsonAsync(api.Options.Queries.Values.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).Select(q => new {
            name = q.Name,
            description = q.Description,
            mode = q.Mode.ToString().ToLowerInvariant(),
            maxRows = q.MaxRows,
            parameters = q.Parameters.Select(p => new {
                name = p.Name,
                type = p.Type.ToString().ToLowerInvariant(),
                required = p.Required,
                defaultValue = p.Default
            })
        }), context.RequestAborted);
    }

    private static async Task RunQuery(HttpContext context, ApiServices api, string name) {
        api.Authorize(context, ApiAction.Query, name: name);
        CancellationToken cancellationToken = context.RequestAborted;

        if (!api.Options.Queries.TryGetValue(name, out NamedQuery? query)) {
            throw ApiException.NotFound($"There is no query named '{name}'. GET /queries lists them.");
        }

        bool isPost = HttpMethods.IsPost(context.Request.Method);
        if (query.Mode == QueryMode.Write) {
            api.Authorize(context, ApiAction.Write, name: name);
            if (!isPost) {
                throw ApiException.BadRequest($"The query '{name}' changes data, so call it with POST.");
            }
        }

        ResultFormat format = ResultWriter.Negotiate(context);
        Dictionary<string, string> supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in context.Request.Query) {
            if (!Reserved.Contains(pair.Key)) {
                supplied[pair.Key] = pair.Value.ToString();
            }
        }
        if (isPost) {
            await ReadBodyParametersAsync(context, supplied);
        }

        Dictionary<string, object?> values = query.Bind(supplied);

        int maxRows = api.Options.MaxRows;
        if (query.MaxRows.HasValue) {
            maxRows = Math.Min(maxRows, query.MaxRows.Value);
        }
        string maxRowsText = context.Request.Query["maxRows"].ToString();
        if (!string.IsNullOrWhiteSpace(maxRowsText)) {
            if (!int.TryParse(maxRowsText, out int requested) || requested < 1) {
                throw ApiException.BadRequest("maxRows must be a whole number, 1 or more.");
            }
            maxRows = Math.Min(maxRows, requested);
        }

        ApiServer.NoteSql(context, query.Sql);
        AccessMode mode = query.Mode == QueryMode.Write ? AccessMode.Write : AccessMode.Read;

        using (DatabaseSession session = await api.Gate.EnterAsync(mode, cancellationToken, context))
        using (DuckDBCommand command = session.CreateCommand(query.Sql, null, values))
        using (session.StartTimer(command))
        using (DbDataReader reader = command.ExecuteReader()) {
            await ResultWriter.WriteAsync(context, reader, format, maxRows, cancellationToken);
            session.Commit();
        }
    }

    // A JSON object body supplies parameters; numbers and booleans are turned into their text form.
    private static async Task ReadBodyParametersAsync(HttpContext context, Dictionary<string, string> supplied) {
        string body = await RequestBody.ReadTextAsync(context);
        if (string.IsNullOrWhiteSpace(body)) {
            return;
        }

        using (JsonDocument document = JsonDocument.Parse(body)) {
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                throw ApiException.BadRequest("The body must be a JSON object of parameter values, such as {\"customerId\": 1234}.");
            }
            foreach (JsonProperty property in document.RootElement.EnumerateObject()) {
                switch (property.Value.ValueKind) {
                    case JsonValueKind.Null:
                        break;
                    case JsonValueKind.String:
                        supplied[property.Name] = property.Value.GetString() ?? "";
                        break;
                    case JsonValueKind.True:
                        supplied[property.Name] = "true";
                        break;
                    case JsonValueKind.False:
                        supplied[property.Name] = "false";
                        break;
                    case JsonValueKind.Number:
                        supplied[property.Name] = property.Value.GetRawText();
                        break;
                    default:
                        throw ApiException.BadRequest($"The parameter '{property.Name}' must be a string, number or boolean.");
                }
            }
        }
    }
}
