using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Http;

namespace DataPuddle.Api;

/// <summary>
/// Streams a query result as an Apache Arrow IPC stream, the format analytics tools (pandas, Polars, Spark,
/// R, DuckDB itself) read directly with no parsing. Rows are sent in batches, so large results are not held in memory.
/// Numbers, booleans, text, dates and timestamps keep their types. Decimals up to 28 digits stay decimals.
/// Everything else (times, binary, UUIDs, huge integers, lists, structs and maps) is sent as text:
/// ISO 8601 for times, base64 for binary, JSON for nested values.
/// </summary>
public static class ArrowResultWriter {
    public const string ContentType = "application/vnd.apache.arrow.stream";

    private const int BatchRows = 10000;

    private static readonly Regex DecimalPattern = new Regex(@"^DECIMAL\((\d+),\s*(\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task WriteAsync(HttpContext context, DbDataReader reader, string[] names, int maxRows, CancellationToken cancellationToken) {
        context.Response.ContentType = ContentType;

        List<Func<ColumnBuilder>> factories = new List<Func<ColumnBuilder>>();
        Schema.Builder schemaBuilder = new Schema.Builder();
        for (int i = 0; i < names.Length; i++) {
            IArrowType arrowType;
            Func<ColumnBuilder> factory = CreateFactory(reader.GetFieldType(i), reader.GetDataTypeName(i), out arrowType);
            factories.Add(factory);
            schemaBuilder.Field(new Field(names[i], arrowType, true));
        }
        Schema schema = schemaBuilder.Build();

        long count = 0;
        using (ArrowStreamWriter writer = new ArrowStreamWriter(context.Response.Body, schema, true)) {
            await writer.WriteStartAsync(cancellationToken);

            bool more = true;
            while (more) {
                List<ColumnBuilder> columns = factories.Select(f => f()).ToList();
                int rows = 0;
                while (rows < BatchRows && count < maxRows) {
                    if (!reader.Read()) {
                        more = false;
                        break;
                    }
                    for (int i = 0; i < columns.Count; i++) {
                        columns[i].Append(reader.IsDBNull(i) ? null : reader.GetValue(i));
                    }
                    rows++;
                    count++;
                }
                if (count >= maxRows) {
                    more = false;
                }

                if (rows > 0) {
                    List<IArrowArray> arrays = columns.Select(c => c.Build()).ToList();
                    using (RecordBatch batch = new RecordBatch(schema, arrays, rows)) {
                        await writer.WriteRecordBatchAsync(batch, cancellationToken);
                    }
                }
            }

            await writer.WriteEndAsync(cancellationToken);
        }

        context.Items[ResultWriter.RowsItem] = count;
    }

    // ---------- Column builders ----------

    /// <summary>Collects the values of one column for one batch.</summary>
    private abstract class ColumnBuilder {
        public abstract void Append(object? value);
        public abstract IArrowArray Build();
    }

    private sealed class DelegateColumn : ColumnBuilder {
        private readonly Action<object?> _append;
        private readonly Func<IArrowArray> _build;

        public DelegateColumn(Action<object?> append, Func<IArrowArray> build) {
            _append = append;
            _build = build;
        }

        public override void Append(object? value) {
            _append(value);
        }

        public override IArrowArray Build() {
            return _build();
        }
    }

    private static Func<ColumnBuilder> CreateFactory(Type clrType, string databaseType, out IArrowType arrowType) {
        CultureInfo inv = CultureInfo.InvariantCulture;

        if (clrType == typeof(bool)) {
            arrowType = BooleanType.Default;
            return () => {
                BooleanArray.Builder b = new BooleanArray.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToBoolean(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(sbyte)) {
            arrowType = Int8Type.Default;
            return () => {
                Int8Array.Builder b = new Int8Array.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToSByte(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(byte)) {
            arrowType = UInt8Type.Default;
            return () => {
                UInt8Array.Builder b = new UInt8Array.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToByte(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(short)) {
            arrowType = Int16Type.Default;
            return () => {
                Int16Array.Builder b = new Int16Array.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToInt16(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(ushort)) {
            arrowType = UInt16Type.Default;
            return () => {
                UInt16Array.Builder b = new UInt16Array.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToUInt16(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(int)) {
            arrowType = Int32Type.Default;
            return () => {
                Int32Array.Builder b = new Int32Array.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToInt32(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(uint)) {
            arrowType = UInt32Type.Default;
            return () => {
                UInt32Array.Builder b = new UInt32Array.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToUInt32(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(long)) {
            arrowType = Int64Type.Default;
            return () => {
                Int64Array.Builder b = new Int64Array.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToInt64(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(ulong)) {
            arrowType = UInt64Type.Default;
            return () => {
                UInt64Array.Builder b = new UInt64Array.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToUInt64(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(float)) {
            arrowType = FloatType.Default;
            return () => {
                FloatArray.Builder b = new FloatArray.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToSingle(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(double)) {
            arrowType = DoubleType.Default;
            return () => {
                DoubleArray.Builder b = new DoubleArray.Builder();
                return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToDouble(v, inv)); } }, () => b.Build());
            };
        }
        if (clrType == typeof(decimal)) {
            Match match = DecimalPattern.Match(databaseType);
            if (match.Success) {
                int precision = int.Parse(match.Groups[1].Value, inv);
                int scale = int.Parse(match.Groups[2].Value, inv);
                if (precision <= 28) {
                    Decimal128Type decimalType = new Decimal128Type(precision, scale);
                    arrowType = decimalType;
                    return () => {
                        Decimal128Array.Builder b = new Decimal128Array.Builder(decimalType);
                        return new DelegateColumn(v => { if (v == null) { b.AppendNull(); } else { b.Append(Convert.ToDecimal(v, inv)); } }, () => b.Build());
                    };
                }
            }
        }
        if (clrType == typeof(DateOnly) || (clrType == typeof(DateTime) && databaseType.Equals("DATE", StringComparison.OrdinalIgnoreCase))) {
            arrowType = Date32Type.Default;
            return () => {
                Date32Array.Builder b = new Date32Array.Builder();
                return new DelegateColumn(v => {
                    if (v == null) {
                        b.AppendNull();
                    } else if (v is DateOnly date) {
                        b.Append(DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc));
                    } else {
                        b.Append(DateTime.SpecifyKind(Convert.ToDateTime(v, inv).Date, DateTimeKind.Utc));
                    }
                }, () => b.Build());
            };
        }
        if (clrType == typeof(DateTime) || clrType == typeof(DateTimeOffset)) {
            // Timestamps are kept as UTC: the local copy runs with its time zone set to UTC.
            TimestampType timestampType = new TimestampType(TimeUnit.Microsecond, "UTC");
            arrowType = timestampType;
            return () => {
                TimestampArray.Builder b = new TimestampArray.Builder(timestampType);
                return new DelegateColumn(v => {
                    if (v == null) {
                        b.AppendNull();
                    } else if (v is DateTimeOffset offset) {
                        b.Append(offset);
                    } else {
                        b.Append(new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(v, inv), DateTimeKind.Utc)));
                    }
                }, () => b.Build());
            };
        }

        // Text, and everything with no Arrow type of its own above.
        arrowType = StringType.Default;
        return () => {
            StringArray.Builder b = new StringArray.Builder();
            return new DelegateColumn(v => {
                if (v == null) {
                    b.AppendNull();
                } else {
                    b.Append(ResultWriter.ToPlainText(v));
                }
            }, () => b.Build());
        };
    }
}
