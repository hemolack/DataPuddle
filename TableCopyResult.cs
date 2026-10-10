namespace DataPuddle;

/// <summary>
/// Outcome of copying one table: row counts at each stage and how long it took.
/// </summary>
public sealed record TableCopyResult(
    string Schema,
    string Table,
    long RowsLoaded,
    long SqlCount,
    long LocalCount,
    long? DeltaCount,
    TimeSpan Elapsed) {

    /// <summary>The table name qualified with its schema, as schema.table.</summary>
    public string FullName {
        get {
            return Schema + "." + Table;
        }
    }

    /// <summary>
    /// True when SQL Server and DuckDB report the same row count, and so do the Delta files if a Delta
    /// export was written (<see cref="DeltaCount"/> is null when it was not).
    /// </summary>
    public bool CountsMatch {
        get {
            return SqlCount == LocalCount && (!DeltaCount.HasValue || DeltaCount.Value == LocalCount);
        }
    }
}
