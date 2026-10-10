using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;

namespace DataPuddle;

internal enum ColKind {
    Bool, Int16, Int32, Int64, Float, Double, Decimal,
    String, Date, Time, DateTime, DateTimeOffset, Guid, Binary
}

internal sealed record ColumnMap(
    string Name,
    ColKind Kind,
    string DuckType,
    string DeltaType,
    string SelectExpr,
    bool Nullable);

/// <summary>A column as it is written to a Delta table: its Delta type and the DuckDB expression that produces it.</summary>
internal sealed record DeltaColumn(
    string Name,
    string DeltaType,
    string SelectExpr,
    bool Nullable);

/// <summary>
/// Copies tables from SQL Server into a local DuckDB file. Delta Lake export is optional: it happens
/// during the copy when <see cref="CopyOptions.WriteDelta"/> is on, and on demand for any DuckDB table
/// through <see cref="ExportDelta"/>. After <see cref="Open"/> the DuckDB connection is available through
/// <see cref="Connection"/> for further SQL work (for example local ETL).
/// </summary>
public sealed class LocalStore : IDisposable {
    private readonly CopyOptions _options;
    private SqlConnection? _sql;
    private DuckDBConnection? _duck;

    public LocalStore(CopyOptions options) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Receives progress messages. Does nothing by default.</summary>
    public Action<string> Log { get; set; } = _ => { };

    public CopyOptions Options {
        get {
            return _options;
        }
    }

    public string DatabasePath {
        get {
            return _options.DatabasePath;
        }
    }

    /// <summary>The open DuckDB connection. Throws if <see cref="Open"/> has not been called.</summary>
    public DuckDBConnection Connection {
        get {
            if (_duck == null) {
                throw new InvalidOperationException("Call Open() before using the connection.");
            }
            return _duck;
        }
    }

    public string DeltaDirectory(string schema, string table) {
        return Path.Combine(_options.OutputDirectory, "delta", schema, table);
    }

    // ---------- Lifecycle ----------

    /// <summary>
    /// Opens the DuckDB file. With recreate = true (the default) any existing file is deleted first;
    /// with recreate = false an existing file is opened as it is.
    /// </summary>
    public void Open(bool recreate = true) {
        if (_duck != null) {
            throw new InvalidOperationException("The DuckDB file is already open.");
        }

        Directory.CreateDirectory(_options.OutputDirectory);
        if (recreate) {
            DeleteIfExists(DatabasePath);
            DeleteIfExists(DatabasePath + ".wal");
        }

        DuckDBConnection duck = new DuckDBConnection($"Data Source={DatabasePath}");
        duck.Open();
        _duck = duck;

        Execute("SET TimeZone='UTC'");
    }

    public void Dispose() {
        _sql?.Dispose();
        _sql = null;
        _duck?.Dispose();
        _duck = null;
    }

    // ---------- Copying ----------

    /// <summary>
    /// Clones every table in <see cref="CopyOptions.Tables"/> (schema.table names).
    /// Returns an empty list when no tables are configured.
    /// </summary>
    public IReadOnlyList<TableCopyResult> CopyAll() {
        if (_options.Tables.Count == 0) {
            return new List<TableCopyResult>();
        }
        return Clone(_options.Tables);
    }

    /// <summary>
    /// Clones the given tables, each written as schema.table, from SQL Server into DuckDB. Every table
    /// gets the same treatment as the startup tables: DuckDB schema and table creation, row load,
    /// row-count verification, and a Delta export when <see cref="CopyOptions.WriteDelta"/> is on.
    /// Names are validated before anything is copied.
    /// </summary>
    public IReadOnlyList<TableCopyResult> Clone(IEnumerable<string> qualifiedTableNames) {
        List<(string Schema, string Table)> tables = ParseTableList(qualifiedTableNames);
        List<TableCopyResult> results = new List<TableCopyResult>();
        foreach ((string Schema, string Table) t in tables) {
            results.Add(CopyTable(t.Schema, t.Table));
        }
        return results;
    }

    /// <summary>Clones a comma-separated list such as "dbo.visit, dbo.payment".</summary>
    public IReadOnlyList<TableCopyResult> Clone(string tableList) {
        return Clone(tableList.Split(','));
    }

    /// <summary>
    /// Parses a comma-separated list of schema.table names. Duplicates (ignoring case) are dropped.
    /// Throws <see cref="FormatException"/> if any entry is not in schema.table format or the list is empty.
    /// </summary>
    public static List<(string Schema, string Table)> ParseTableList(string tableList) {
        return ParseTableList(tableList.Split(','));
    }

    public static List<(string Schema, string Table)> ParseTableList(IEnumerable<string> qualifiedTableNames) {
        List<(string Schema, string Table)> tables = new List<(string Schema, string Table)>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in qualifiedTableNames) {
            if (string.IsNullOrWhiteSpace(name)) {
                continue;
            }
            (string Schema, string Table) parsed = ParseTableName(name);
            if (seen.Add(parsed.Schema + "." + parsed.Table)) {
                tables.Add(parsed);
            }
        }
        if (tables.Count == 0) {
            throw new FormatException("No tables specified. Use schema.table, separated by commas.");
        }
        return tables;
    }

    /// <summary>Parses schema.table. Square brackets around either part are allowed and removed.</summary>
    public static (string Schema, string Table) ParseTableName(string qualifiedName) {
        string trimmed = qualifiedName.Trim();
        string[] parts = trimmed.Split('.');
        if (parts.Length != 2) {
            throw new FormatException($"'{trimmed}' is not in schema.table format.");
        }

        string schema = StripBrackets(parts[0].Trim());
        string table = StripBrackets(parts[1].Trim());
        if (!ValidNamePattern.IsMatch(schema) || !ValidNamePattern.IsMatch(table)) {
            throw new FormatException(
                $"'{trimmed}' is not a valid schema.table name (letters, digits, underscore, space, $, #, @ and - are allowed).");
        }
        return (schema, table);
    }

    private static readonly Regex ValidNamePattern = new Regex(@"^[\p{L}\p{N}_$#@ \-]+$", RegexOptions.Compiled);

    private static string StripBrackets(string name) {
        if (name.Length >= 2 && name[0] == '[' && name[name.Length - 1] == ']') {
            return name.Substring(1, name.Length - 2);
        }
        return name;
    }

    /// <summary>
    /// Copies one schema.table from SQL Server into DuckDB (replacing any existing copy) and compares
    /// row counts between SQL Server and DuckDB. When <see cref="CopyOptions.WriteDelta"/> is on it also
    /// writes the Delta export and includes the Delta files in the comparison.
    /// </summary>
    public TableCopyResult CopyTable(string schema, string table) {
        SqlConnection sql = GetSqlConnection();
        Stopwatch watch = Stopwatch.StartNew();

        Log($"== {schema}.{table} ==");
        List<ColumnMap> cols = ReadSchema(sql, schema, table);

        CreateDuckTable(schema, table, cols);
        long loaded = LoadRows(sql, schema, table, cols);
        Log($"  loaded {loaded:N0} rows into the local database");

        string deltaDir = DeltaDirectory(schema, table);
        if (_options.WriteDelta) {
            WriteDelta(schema, table, ToDeltaColumns(cols), deltaDir);
        }

        long sqlCount = ScalarSql(sql, $"SELECT COUNT_BIG(*) FROM {QuoteSql(schema)}.{QuoteSql(table)}{BuildWhere(schema, table)}");
        long duckCount = ScalarLong($"SELECT COUNT(*) FROM {QuoteDuck(schema)}.{QuoteDuck(table)}");
        long? deltaCount = null;
        if (_options.WriteDelta) {
            deltaCount = CountDeltaRows(deltaDir);
        }

        watch.Stop();
        TableCopyResult result = new TableCopyResult(schema, table, loaded, sqlCount, duckCount, deltaCount, watch.Elapsed);
        string deltaText = deltaCount.HasValue ? $"  delta={deltaCount.Value:N0}" : "";
        Log($"  row counts  sql={sqlCount:N0}  local={duckCount:N0}{deltaText}  {(result.CountsMatch ? "OK" : "MISMATCH")}");
        Log($"  elapsed {FormatElapsed(result.Elapsed)}");
        return result;
    }

    public static string FormatElapsed(TimeSpan elapsed) {
        return elapsed.ToString(@"hh\:mm\:ss\.fff");
    }

    // ---------- DuckDB helpers for callers ----------

    /// <summary>Runs a statement that returns no rows.</summary>
    public void Execute(string sqlText) {
        using (DuckDBCommand cmd = Connection.CreateCommand()) {
            cmd.CommandText = sqlText;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Runs a query and returns its first value as a long.</summary>
    public long ScalarLong(string sqlText) {
        using (DuckDBCommand cmd = Connection.CreateCommand()) {
            cmd.CommandText = sqlText;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    // ---------- SQL Server side ----------

    private SqlConnection GetSqlConnection() {
        if (_sql != null) {
            return _sql;
        }
        if (string.IsNullOrWhiteSpace(_options.SqlConnectionString)) {
            throw new InvalidOperationException("CopyOptions.SqlConnectionString is required to copy from SQL Server.");
        }

        SqlConnection sql = new SqlConnection(_options.SqlConnectionString);
        sql.Open();
        _sql = sql;
        return sql;
    }

    private long ScalarSql(SqlConnection sql, string sqlText) {
        using (SqlCommand cmd = new SqlCommand(sqlText, sql)) {
            cmd.CommandTimeout = 0;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    // Single source of truth for the SQL Server filter, used by both the load query and the
    // verification count so they cannot drift apart.
    // Table filters are looked up by "schema.table" first, then by bare table name.
    private string BuildWhere(string schema, string table) {
        List<string> conditions = new List<string>();
        string? extra;
        if ((_options.TableFilters.TryGetValue(schema + "." + table, out extra) || _options.TableFilters.TryGetValue(table, out extra))
            && !string.IsNullOrWhiteSpace(extra)) {
            conditions.Add(extra);
        }
        return conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
    }

    // ---------- Schema discovery and type mapping ----------

    private static List<ColumnMap> ReadSchema(SqlConnection sql, string schema, string table) {
        const string query = @"
            SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH,
                   NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t
            ORDER BY ORDINAL_POSITION";

        List<ColumnMap> cols = new List<ColumnMap>();
        using (SqlCommand cmd = new SqlCommand(query, sql)) {
            cmd.Parameters.AddWithValue("@s", schema);
            cmd.Parameters.AddWithValue("@t", table);
            using (SqlDataReader r = cmd.ExecuteReader()) {
                while (r.Read()) {
                    string name = r.GetString(0);
                    string dataType = r.GetString(1);
                    int precision = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                    int scale = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4));
                    bool nullable = string.Equals(r.GetString(5), "YES", StringComparison.OrdinalIgnoreCase);
                    cols.Add(MapColumn(name, dataType, precision, scale, nullable));
                }
            }
        }

        if (cols.Count == 0) {
            throw new InvalidOperationException($"Table {schema}.{table} not found or has no columns.");
        }
        return cols;
    }

    private static ColumnMap MapColumn(string name, string dataType, int precision, int scale, bool nullable) {
        string q = QuoteDuck(name);
        switch (dataType.ToLowerInvariant()) {
            case "bit":
                return new ColumnMap(name, ColKind.Bool, "BOOLEAN", "boolean", q, nullable);
            case "tinyint":
            case "smallint":
                return new ColumnMap(name, ColKind.Int16, "SMALLINT", "short", q, nullable);
            case "int":
                return new ColumnMap(name, ColKind.Int32, "INTEGER", "integer", q, nullable);
            case "bigint":
                return new ColumnMap(name, ColKind.Int64, "BIGINT", "long", q, nullable);
            case "real":
                return new ColumnMap(name, ColKind.Float, "FLOAT", "float", q, nullable);
            case "float":
                return precision <= 24
                    ? new ColumnMap(name, ColKind.Float, "FLOAT", "float", q, nullable)
                    : new ColumnMap(name, ColKind.Double, "DOUBLE", "double", q, nullable);
            case "decimal":
            case "numeric":
                return new ColumnMap(name, ColKind.Decimal, $"DECIMAL({precision},{scale})", $"decimal({precision},{scale})", q, nullable);
            case "money":
                return new ColumnMap(name, ColKind.Decimal, "DECIMAL(19,4)", "decimal(19,4)", q, nullable);
            case "smallmoney":
                return new ColumnMap(name, ColKind.Decimal, "DECIMAL(10,4)", "decimal(10,4)", q, nullable);
            case "date":
                return new ColumnMap(name, ColKind.Date, "DATE", "date", q, nullable);
            case "time":
                // Delta has no TIME type, so the Delta copy stores it as a string.
                return new ColumnMap(name, ColKind.Time, "TIME", "string", $"CAST({q} AS VARCHAR)", nullable);
            case "datetime":
            case "datetime2":
            case "smalldatetime":
                // Delta "timestamp" is UTC-adjusted; values are treated as UTC in the Delta copy.
                return new ColumnMap(name, ColKind.DateTime, "TIMESTAMP", "timestamp", $"CAST({q} AS TIMESTAMPTZ)", nullable);
            case "datetimeoffset":
                return new ColumnMap(name, ColKind.DateTimeOffset, "TIMESTAMPTZ", "timestamp", q, nullable);
            case "char":
            case "varchar":
            case "nchar":
            case "nvarchar":
            case "text":
            case "ntext":
            case "xml":
            case "sysname":
                return new ColumnMap(name, ColKind.String, "VARCHAR", "string", q, nullable);
            case "uniqueidentifier":
                return new ColumnMap(name, ColKind.Guid, "UUID", "string", $"CAST({q} AS VARCHAR)", nullable);
            case "binary":
            case "varbinary":
            case "image":
            case "timestamp": // rowversion
                return new ColumnMap(name, ColKind.Binary, "BLOB", "binary", q, nullable);
            default:
                throw new NotSupportedException($"Column '{name}' has unsupported SQL Server type '{dataType}'.");
        }
    }

    // ---------- DuckDB load ----------

    private void CreateDuckTable(string schema, string table, List<ColumnMap> cols) {
        IEnumerable<string> defs = cols.Select(c =>
            $"{QuoteDuck(c.Name)} {c.DuckType}{(c.Nullable ? "" : " NOT NULL")}");
        Execute($"CREATE SCHEMA IF NOT EXISTS {QuoteDuck(schema)}");
        Execute($"CREATE OR REPLACE TABLE {QuoteDuck(schema)}.{QuoteDuck(table)} ({string.Join(", ", defs)})");
    }

    private long LoadRows(SqlConnection sql, string schema, string table, List<ColumnMap> cols) {
        string colList = string.Join(", ", cols.Select(c => QuoteSql(c.Name)));
        string query = $"SELECT {colList} FROM {QuoteSql(schema)}.{QuoteSql(table)}{BuildWhere(schema, table)}";
        long count = 0;

        using (SqlCommand cmd = new SqlCommand(query, sql)) {
            cmd.CommandTimeout = 0;
            using (SqlDataReader reader = cmd.ExecuteReader()) {
                using (DuckDBAppender appender = Connection.CreateAppender(schema, table)) {
                    while (reader.Read()) {
                        IDuckDBAppenderRow row = appender.CreateRow();
                        for (int i = 0; i < cols.Count; i++) {
                            AppendValue(row, reader.GetValue(i), cols[i].Kind);
                        }
                        row.EndRow();
                        count++;
                    }
                }
            }
        }
        return count;
    }

    private static void AppendValue(IDuckDBAppenderRow row, object value, ColKind kind) {
        if (value is DBNull) {
            row.AppendNullValue();
            return;
        }

        switch (kind) {
            case ColKind.Bool: row.AppendValue(Convert.ToBoolean(value)); break;
            case ColKind.Int16: row.AppendValue(Convert.ToInt16(value)); break;
            case ColKind.Int32: row.AppendValue(Convert.ToInt32(value)); break;
            case ColKind.Int64: row.AppendValue(Convert.ToInt64(value)); break;
            case ColKind.Float: row.AppendValue(Convert.ToSingle(value)); break;
            case ColKind.Double: row.AppendValue(Convert.ToDouble(value)); break;
            case ColKind.Decimal: row.AppendValue(Convert.ToDecimal(value)); break;
            case ColKind.String: row.AppendValue(Convert.ToString(value)); break;
            case ColKind.Date: row.AppendValue(DateOnly.FromDateTime((DateTime)value)); break;
            case ColKind.Time: row.AppendValue(TimeOnly.FromTimeSpan((TimeSpan)value)); break;
            case ColKind.DateTime: row.AppendValue((DateTime)value); break;
            case ColKind.DateTimeOffset: row.AppendValue((DateTimeOffset)value); break;
            case ColKind.Guid: row.AppendValue((Guid)value); break;
            case ColKind.Binary: row.AppendValue((byte[])value); break;
            default: throw new NotSupportedException(kind.ToString());
        }
    }

    // ---------- Delta Lake export ----------

    /// <summary>
    /// Writes a Delta Lake export of a table that already exists in DuckDB, whether it was cloned from
    /// SQL Server or created by your own SQL. Column types are read from DuckDB, so no SQL Server
    /// connection is needed. Any previous export of the same table is replaced. Throws
    /// <see cref="NotSupportedException"/> for column types with no Delta mapping (nested types such as
    /// LIST, STRUCT and MAP, and INTERVAL).
    /// </summary>
    public ExportResult ExportDelta(string schema, string table) {
        Stopwatch watch = Stopwatch.StartNew();
        Log($"== export delta {schema}.{table} ==");

        List<DeltaColumn> cols = ReadDuckColumns(schema, table);
        string deltaDir = DeltaDirectory(schema, table);
        WriteDelta(schema, table, cols, deltaDir);

        long duckCount = ScalarLong($"SELECT COUNT(*) FROM {QuoteDuck(schema)}.{QuoteDuck(table)}");
        long deltaCount = CountDeltaRows(deltaDir);

        watch.Stop();
        ExportResult result = new ExportResult(schema, table, "delta", deltaDir, duckCount, deltaCount, watch.Elapsed);
        Log($"  row counts  local={duckCount:N0}  delta={deltaCount:N0}  {(result.CountsMatch ? "OK" : "MISMATCH")}");
        Log($"  elapsed {FormatElapsed(result.Elapsed)}");
        return result;
    }

    /// <summary>Default location of a delimited-file export: output/export/schema.table.extension.</summary>
    public string ExportFilePath(string schema, string table, string extension) {
        return Path.Combine(_options.OutputDirectory, "export", schema + "." + table + "." + extension);
    }

    /// <summary>
    /// Exports a table that already exists in DuckDB. Delta writes a folder under delta/schema/table;
    /// csv, tsv and delimited write a single file with a header row under export/. The delimiter is
    /// required for <see cref="ExportFormat.Delimited"/> and ignored for the other formats.
    /// </summary>
    public ExportResult Export(ExportFormat format, string schema, string table, char? delimiter = null) {
        switch (format) {
            case ExportFormat.Delta:
                return ExportDelta(schema, table);
            case ExportFormat.Csv:
                return ExportDelimitedFile(format, schema, table, ',', "csv");
            case ExportFormat.Tsv:
                return ExportDelimitedFile(format, schema, table, '\t', "tsv");
            case ExportFormat.Delimited:
                if (!delimiter.HasValue) {
                    throw new ArgumentException("A delimiter character is required for the delimited format.");
                }
                return ExportDelimitedFile(format, schema, table, delimiter.Value, "txt");
            default:
                throw new NotSupportedException(format.ToString());
        }
    }

    /// <summary>Exports a DuckDB table as delimited text using the given delimiter character.</summary>
    public ExportResult ExportDelimited(string schema, string table, char delimiter) {
        return ExportDelimitedFile(ExportFormat.Delimited, schema, table, delimiter, "txt");
    }

    private ExportResult ExportDelimitedFile(ExportFormat format, string schema, string table, char delimiter, string extension) {
        ValidateDelimiter(delimiter);
        Stopwatch watch = Stopwatch.StartNew();
        string formatName = format.ToString().ToLowerInvariant();
        Log($"== export {formatName} {schema}.{table} ==");

        // Counting first also proves the table exists before any file is touched.
        long duckCount = ScalarLong($"SELECT COUNT(*) FROM {QuoteDuck(schema)}.{QuoteDuck(table)}");
        string path = ExportFilePath(schema, table, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long written = ScalarLong(
            $"COPY (SELECT * FROM {QuoteDuck(schema)}.{QuoteDuck(table)}) TO '{EscapePath(path)}' " +
            $"(FORMAT CSV, HEADER true, DELIMITER '{EscapeLiteral(delimiter.ToString())}')");

        watch.Stop();
        ExportResult result = new ExportResult(schema, table, formatName, path, duckCount, written, watch.Elapsed);
        Log($"  wrote {path}");
        Log($"  row counts  local={duckCount:N0}  written={written:N0}  {(result.CountsMatch ? "OK" : "MISMATCH")}");
        Log($"  elapsed {FormatElapsed(result.Elapsed)}");
        return result;
    }

    internal static void ValidateDelimiter(char delimiter) {
        if (delimiter == '"' || delimiter == '\r' || delimiter == '\n' || (char.IsControl(delimiter) && delimiter != '\t')) {
            throw new ArgumentException("The delimiter cannot be a double quote, a line break, or a control character other than tab.");
        }
    }

    private static List<DeltaColumn> ToDeltaColumns(List<ColumnMap> cols) {
        return cols.Select(c => new DeltaColumn(c.Name, c.DeltaType, c.SelectExpr, c.Nullable)).ToList();
    }

    private long CountDeltaRows(string deltaDir) {
        if (Directory.GetFiles(deltaDir, "*.parquet").Length == 0) {
            return 0;
        }
        return ScalarLong($"SELECT COUNT(*) FROM read_parquet('{EscapePath(deltaDir)}/*.parquet')");
    }

    private List<DeltaColumn> ReadDuckColumns(string schema, string table) {
        string query =
            "SELECT column_name, data_type, is_nullable FROM information_schema.columns " +
            $"WHERE lower(table_schema) = lower('{EscapeLiteral(schema)}') " +
            $"AND lower(table_name) = lower('{EscapeLiteral(table)}') " +
            "ORDER BY ordinal_position";

        List<DeltaColumn> cols = new List<DeltaColumn>();
        using (DuckDBCommand cmd = Connection.CreateCommand()) {
            cmd.CommandText = query;
            using (DbDataReader reader = cmd.ExecuteReader()) {
                while (reader.Read()) {
                    string name = reader.GetString(0);
                    string dataType = reader.GetString(1);
                    bool nullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase);
                    cols.Add(MapDuckColumn(name, dataType, nullable));
                }
            }
        }

        if (cols.Count == 0) {
            throw new InvalidOperationException($"Table {schema}.{table} was not found in the local database.");
        }
        return cols;
    }

    private static readonly Regex DecimalTypePattern = new Regex(@"^DECIMAL\((\d+),\s*(\d+)\)$", RegexOptions.Compiled);

    private static DeltaColumn MapDuckColumn(string name, string dataType, bool nullable) {
        string q = QuoteDuck(name);
        string type = dataType.Trim().ToUpperInvariant();

        Match decimalMatch = DecimalTypePattern.Match(type);
        if (decimalMatch.Success) {
            return new DeltaColumn(name, $"decimal({decimalMatch.Groups[1].Value},{decimalMatch.Groups[2].Value})", q, nullable);
        }

        switch (type) {
            case "BOOLEAN":
                return new DeltaColumn(name, "boolean", q, nullable);
            case "TINYINT":
                return new DeltaColumn(name, "byte", q, nullable);
            case "SMALLINT":
            case "UTINYINT":
                return new DeltaColumn(name, "short", q, nullable);
            case "INTEGER":
            case "USMALLINT":
                return new DeltaColumn(name, "integer", q, nullable);
            case "BIGINT":
            case "UINTEGER":
                return new DeltaColumn(name, "long", q, nullable);
            case "UBIGINT":
                return new DeltaColumn(name, "decimal(20,0)", $"CAST({q} AS DECIMAL(20,0))", nullable);
            case "HUGEINT":
                return new DeltaColumn(name, "decimal(38,0)", $"CAST({q} AS DECIMAL(38,0))", nullable);
            case "FLOAT":
                return new DeltaColumn(name, "float", q, nullable);
            case "DOUBLE":
                return new DeltaColumn(name, "double", q, nullable);
            case "VARCHAR":
                return new DeltaColumn(name, "string", q, nullable);
            case "UUID":
            case "TIME":
            case "TIME WITH TIME ZONE":
                // Delta has no UUID or TIME type, so these are stored as strings.
                return new DeltaColumn(name, "string", $"CAST({q} AS VARCHAR)", nullable);
            case "DATE":
                return new DeltaColumn(name, "date", q, nullable);
            case "TIMESTAMP":
            case "TIMESTAMP_S":
            case "TIMESTAMP_MS":
            case "TIMESTAMP_NS":
                // Delta "timestamp" is UTC-adjusted; values are treated as UTC in the Delta copy.
                return new DeltaColumn(name, "timestamp", $"CAST({q} AS TIMESTAMPTZ)", nullable);
            case "TIMESTAMP WITH TIME ZONE":
                return new DeltaColumn(name, "timestamp", q, nullable);
            case "BLOB":
                return new DeltaColumn(name, "binary", q, nullable);
            default:
                if (type.StartsWith("ENUM", StringComparison.Ordinal)) {
                    return new DeltaColumn(name, "string", $"CAST({q} AS VARCHAR)", nullable);
                }
                throw new NotSupportedException(
                    $"Column '{name}' has DuckDB type '{dataType}', which has no Delta mapping. " +
                    "Cast it (for example to VARCHAR) in a view or table first.");
        }
    }

    private void WriteDelta(string schema, string table, List<DeltaColumn> cols, string deltaDir) {
        if (Directory.Exists(deltaDir)) {
            Directory.Delete(deltaDir, true);
        }
        Directory.CreateDirectory(deltaDir);

        string select = string.Join(", ", cols.Select(c => $"{c.SelectExpr} AS {QuoteDuck(c.Name)}"));
        Execute(
            $"COPY (SELECT {select} FROM {QuoteDuck(schema)}.{QuoteDuck(table)}) TO '{EscapePath(deltaDir)}' " +
            "(FORMAT PARQUET, COMPRESSION SNAPPY, FILE_SIZE_BYTES '256MB', OVERWRITE_OR_IGNORE)");

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<string> lines = new List<string>();

        lines.Add(JsonSerializer.Serialize(new {
            commitInfo = new {
                timestamp = now,
                operation = "WRITE",
                operationParameters = new { mode = "Overwrite" },
                isBlindAppend = false
            }
        }));

        lines.Add(JsonSerializer.Serialize(new {
            protocol = new { minReaderVersion = 1, minWriterVersion = 2 }
        }));

        string schemaString = JsonSerializer.Serialize(new {
            type = "struct",
            fields = cols.Select(c => new {
                name = c.Name,
                type = c.DeltaType,
                nullable = c.Nullable,
                metadata = new { }
            }).ToList()
        });

        lines.Add(JsonSerializer.Serialize(new {
            metaData = new {
                id = Guid.NewGuid().ToString(),
                format = new { provider = "parquet", options = new { } },
                schemaString,
                partitionColumns = Array.Empty<string>(),
                configuration = new { },
                createdTime = now
            }
        }));

        foreach (string file in Directory.GetFiles(deltaDir, "*.parquet").OrderBy(f => f)) {
            FileInfo info = new FileInfo(file);
            long rows = ScalarLong($"SELECT COUNT(*) FROM read_parquet('{EscapePath(file)}')");
            lines.Add(JsonSerializer.Serialize(new {
                add = new {
                    path = info.Name,
                    partitionValues = new { },
                    size = info.Length,
                    modificationTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    dataChange = true,
                    stats = JsonSerializer.Serialize(new { numRecords = rows })
                }
            }));
        }

        string logDir = Path.Combine(deltaDir, "_delta_log");
        Directory.CreateDirectory(logDir);
        File.WriteAllLines(
            Path.Combine(logDir, "00000000000000000000.json"),
            lines,
            new UTF8Encoding(false));

        Log($"  wrote Delta table to {deltaDir}");
    }

    // ---------- Helpers ----------

    private static void DeleteIfExists(string path) {
        if (File.Exists(path)) {
            File.Delete(path);
        }
    }

    private static string QuoteDuck(string name) {
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    private static string QuoteSql(string name) {
        return "[" + name.Replace("]", "]]") + "]";
    }

    private static string EscapeLiteral(string value) {
        return value.Replace("'", "''");
    }

    private static string EscapePath(string path) {
        return path.Replace('\\', '/').Replace("'", "''");
    }
}
