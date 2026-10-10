using System.Buffers;
using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.IO.Pipelines;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace DataPuddle.Api;

public enum ResultFormat {
    Json,
    Ndjson,
    Csv,
    Arrow
}

/// <summary>
/// Streams a query result to the caller one row at a time, so large results never sit in memory.
/// Formats: json (an object with columns and rows), ndjson (one JSON object per line), csv, and arrow
/// (an Apache Arrow stream, see <see cref="ArrowResultWriter"/>).
/// </summary>
public static class ResultWriter {
    public const string RowLimitHeader = "X-DataPuddle-Row-Limit";
    public const string RowsItem = "dp.rows";

    private const int FlushEveryRows = 500;

    /// <summary>Picks the format from ?format= (json, ndjson, csv), else the Accept header, else json.</summary>
    public static ResultFormat Negotiate(HttpContext context) {
        string? requested = context.Request.Query["format"].ToString();
        if (!string.IsNullOrWhiteSpace(requested)) {
            switch (requested.Trim().ToLowerInvariant()) {
                case "json":
                    return ResultFormat.Json;
                case "ndjson":
                case "jsonl":
                    return ResultFormat.Ndjson;
                case "csv":
                    return ResultFormat.Csv;
                case "arrow":
                    return ResultFormat.Arrow;
                default:
                    throw ApiException.BadRequest("format must be json, ndjson, csv or arrow.");
            }
        }

        string accept = context.Request.Headers.Accept.ToString();
        if (accept.Contains("text/csv", StringComparison.OrdinalIgnoreCase)) {
            return ResultFormat.Csv;
        }
        if (accept.Contains(ArrowResultWriter.ContentType, StringComparison.OrdinalIgnoreCase)) {
            return ResultFormat.Arrow;
        }
        if (accept.Contains("application/x-ndjson", StringComparison.OrdinalIgnoreCase)) {
            return ResultFormat.Ndjson;
        }
        return ResultFormat.Json;
    }

    /// <summary>Writes at most maxRows rows of the reader to the response.</summary>
    public static async Task WriteAsync(HttpContext context, DbDataReader reader, ResultFormat format, int maxRows, CancellationToken cancellationToken) {
        string[] names = UniqueNames(reader);
        context.Response.Headers[RowLimitHeader] = maxRows.ToString(CultureInfo.InvariantCulture);
        context.Response.StatusCode = StatusCodes.Status200OK;

        switch (format) {
            case ResultFormat.Csv:
                await WriteCsvAsync(context, reader, names, maxRows, cancellationToken);
                break;
            case ResultFormat.Arrow:
                await ArrowResultWriter.WriteAsync(context, reader, names, maxRows, cancellationToken);
                break;
            case ResultFormat.Ndjson:
                await WriteNdjsonAsync(context, reader, names, maxRows, cancellationToken);
                break;
            default:
                await WriteJsonAsync(context, reader, names, maxRows, cancellationToken);
                break;
        }
    }

    private static async Task WriteJsonAsync(HttpContext context, DbDataReader reader, string[] names, int maxRows, CancellationToken cancellationToken) {
        context.Response.ContentType = "application/json; charset=utf-8";
        PipeWriter pipe = context.Response.BodyWriter;
        long count = 0;
        bool truncated = false;

        using (Utf8JsonWriter writer = new Utf8JsonWriter((IBufferWriter<byte>)pipe)) {
            writer.WriteStartObject();

            writer.WriteStartArray("columns");
            for (int i = 0; i < names.Length; i++) {
                writer.WriteStartObject();
                writer.WriteString("name", names[i]);
                writer.WriteString("type", reader.GetDataTypeName(i));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("rows");
            while (reader.Read()) {
                if (count >= maxRows) {
                    truncated = true;
                    break;
                }
                WriteRowObject(writer, reader, names);
                count++;
                if (count % FlushEveryRows == 0) {
                    writer.Flush();
                    await pipe.FlushAsync(cancellationToken);
                }
            }
            writer.WriteEndArray();

            writer.WriteNumber("rowCount", count);
            writer.WriteBoolean("truncated", truncated);
            writer.WriteEndObject();
            writer.Flush();
        }

        context.Items[RowsItem] = count;
        await pipe.FlushAsync(cancellationToken);
    }

    private static async Task WriteNdjsonAsync(HttpContext context, DbDataReader reader, string[] names, int maxRows, CancellationToken cancellationToken) {
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        PipeWriter pipe = context.Response.BodyWriter;
        long count = 0;

        using (Utf8JsonWriter writer = new Utf8JsonWriter((IBufferWriter<byte>)pipe, new JsonWriterOptions { SkipValidation = true })) {
            while (count < maxRows && reader.Read()) {
                WriteRowObject(writer, reader, names);
                writer.Flush();
                AppendNewline(pipe);
                count++;
                if (count % FlushEveryRows == 0) {
                    await pipe.FlushAsync(cancellationToken);
                }
            }
        }

        context.Items[RowsItem] = count;
        await pipe.FlushAsync(cancellationToken);
    }

    // Span is a ref struct, which an async method cannot hold, so the write is done here.
    private static void AppendNewline(PipeWriter pipe) {
        Span<byte> newline = pipe.GetSpan(1);
        newline[0] = (byte)'\n';
        pipe.Advance(1);
    }

    private static async Task WriteCsvAsync(HttpContext context, DbDataReader reader, string[] names, int maxRows, CancellationToken cancellationToken) {
        context.Response.ContentType = "text/csv; charset=utf-8";
        PipeWriter pipe = context.Response.BodyWriter;
        StringBuilder text = new StringBuilder();
        long count = 0;

        AppendCsvLine(text, names);
        while (count < maxRows && reader.Read()) {
            string[] fields = new string[names.Length];
            for (int i = 0; i < fields.Length; i++) {
                fields[i] = reader.IsDBNull(i) ? "" : ToText(reader.GetValue(i));
            }
            AppendCsvLine(text, fields);
            count++;
            if (text.Length > 32 * 1024) {
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), cancellationToken);
                text.Clear();
            }
        }

        if (text.Length > 0) {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), cancellationToken);
        }
        context.Items[RowsItem] = count;
        await pipe.FlushAsync(cancellationToken);
    }

    private static void AppendCsvLine(StringBuilder text, string[] fields) {
        for (int i = 0; i < fields.Length; i++) {
            if (i > 0) {
                text.Append(',');
            }
            string field = fields[i];
            if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) {
                text.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            } else {
                text.Append(field);
            }
        }
        text.Append("\r\n");
    }

    private static void WriteRowObject(Utf8JsonWriter writer, DbDataReader reader, string[] names) {
        writer.WriteStartObject();
        for (int i = 0; i < names.Length; i++) {
            writer.WritePropertyName(names[i]);
            if (reader.IsDBNull(i)) {
                writer.WriteNullValue();
            } else {
                WriteValue(writer, reader.GetValue(i));
            }
        }
        writer.WriteEndObject();
    }

    /// <summary>Column names, with repeats renamed (id, id_2, id_3) so each row can be an object.</summary>
    public static string[] UniqueNames(DbDataReader reader) {
        string[] names = new string[reader.FieldCount];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++) {
            string name = reader.GetName(i);
            string candidate = name;
            int suffix = 2;
            while (!seen.Add(candidate)) {
                candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }
            names[i] = candidate;
        }
        return names;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value) {
        switch (value) {
            case null:
            case DBNull:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                writer.WriteRawValue(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                break;
            case BigInteger big:
                writer.WriteRawValue(big.ToString(CultureInfo.InvariantCulture));
                break;
            case decimal d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                WriteDouble(writer, f);
                break;
            case double dbl:
                WriteDouble(writer, dbl);
                break;
            case byte[] bytes:
                writer.WriteStringValue(Convert.ToBase64String(bytes));
                break;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary) {
                    writer.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                    WriteValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable list:
                writer.WriteStartArray();
                foreach (object? item in list) {
                    WriteValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(ToText(value));
                break;
        }
    }

    // JSON has no NaN or Infinity, so they become strings rather than breaking the document.
    private static void WriteDouble(Utf8JsonWriter writer, double value) {
        if (double.IsNaN(value) || double.IsInfinity(value)) {
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        } else {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>Text for a value in a text column: nested values (lists, structs, maps) become JSON, everything else <see cref="ToText"/>.</summary>
    public static string ToPlainText(object? value) {
        if (value is IDictionary || (value is IEnumerable && value is not string && value is not byte[])) {
            using (MemoryStream stream = new MemoryStream()) {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream)) {
                    WriteValue(writer, value);
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        return ToText(value);
    }

    /// <summary>Plain text for a value, used by CSV output and for values with no JSON type of their own.</summary>
    public static string ToText(object? value) {
        switch (value) {
            case null:
            case DBNull:
                return "";
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case DateTime dt:
                return dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("O", CultureInfo.InvariantCulture);
            case DateOnly date:
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly time:
                return time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case TimeSpan span:
                return span.ToString("c", CultureInfo.InvariantCulture);
            case byte[] bytes:
                return Convert.ToBase64String(bytes);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString() ?? "";
        }
    }
}
