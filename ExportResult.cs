namespace DataPuddle;

/// <summary>
/// Outcome of exporting one DuckDB table: where it went and whether the row counts agree.
/// </summary>
public sealed record ExportResult(
    string Schema,
    string Table,
    string Format,
    string Location,
    long LocalCount,
    long ExportedCount,
    TimeSpan Elapsed) {

    /// <summary>The table name qualified with its schema, as schema.table.</summary>
    public string FullName {
        get {
            return Schema + "." + Table;
        }
    }

    /// <summary>True when the export holds the same number of rows as the DuckDB table.</summary>
    public bool CountsMatch {
        get {
            return LocalCount == ExportedCount;
        }
    }
}
