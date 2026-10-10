using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace DataPuddle.Api;

/// <summary>Reads request bodies. Always reads before taking the database lock, so a slow upload never blocks other callers.</summary>
public static class RequestBody {
    public static async Task<string> ReadTextAsync(HttpContext context) {
        using (StreamReader reader = new StreamReader(context.Request.Body, Encoding.UTF8)) {
            return await reader.ReadToEndAsync(context.RequestAborted);
        }
    }

    /// <summary>The body as a JSON document. An empty body is an error.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(HttpContext context) {
        string text = await ReadTextAsync(context);
        if (string.IsNullOrWhiteSpace(text)) {
            throw ApiException.BadRequest("The request needs a JSON body.");
        }
        return JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }

    /// <summary>
    /// Rows from the body: a JSON array of objects, a single JSON object, or newline-delimited JSON
    /// (Content-Type application/x-ndjson). Property names match columns without regard to case.
    /// </summary>
    public static async Task<List<Dictionary<string, JsonElement>>> ReadRowsAsync(HttpContext context) {
        string text = await ReadTextAsync(context);
        if (string.IsNullOrWhiteSpace(text)) {
            throw ApiException.BadRequest("The request needs a body with at least one row.");
        }

        List<Dictionary<string, JsonElement>> rows = new List<Dictionary<string, JsonElement>>();
        string contentType = context.Request.ContentType ?? "";
        bool lines = contentType.Contains("ndjson", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("jsonl", StringComparison.OrdinalIgnoreCase);

        if (lines) {
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                using (JsonDocument document = JsonDocument.Parse(line)) {
                    rows.Add(ToRow(document.RootElement));
                }
            }
        } else {
            using (JsonDocument document = JsonDocument.Parse(text)) {
                if (document.RootElement.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement item in document.RootElement.EnumerateArray()) {
                        rows.Add(ToRow(item));
                    }
                } else {
                    rows.Add(ToRow(document.RootElement));
                }
            }
        }

        if (rows.Count == 0) {
            throw ApiException.BadRequest("The request body holds no rows.");
        }
        return rows;
    }

    private static Dictionary<string, JsonElement> ToRow(JsonElement element) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw ApiException.BadRequest("Each row must be a JSON object such as {\"column\": value}.");
        }
        Dictionary<string, JsonElement> row = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (row.ContainsKey(property.Name)) {
                throw ApiException.BadRequest($"The column '{property.Name}' appears twice in one row.");
            }
            row[property.Name] = property.Value.Clone();
        }
        return row;
    }
}
