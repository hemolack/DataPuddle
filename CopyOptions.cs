namespace DataPuddle;

/// <summary>
/// Settings for <see cref="LocalStore"/>.
/// </summary>
public sealed class CopyOptions {
    /// <summary>
    /// SQL Server connection string. Only needed when copying data from SQL Server;
    /// it can be left null when you just want to open an existing DuckDB file.
    /// </summary>
    public string? SqlConnectionString { get; init; }

    /// <summary>Folder that holds the DuckDB file and the delta/ folder.</summary>
    public string OutputDirectory { get; init; } = Path.Combine(Directory.GetCurrentDirectory(), "output");

    /// <summary>Name of the DuckDB file inside <see cref="OutputDirectory"/>.</summary>
    public string DatabaseFileName { get; init; } = "puddle.db";

    /// <summary>
    /// Tables cloned by <see cref="LocalStore.CopyAll"/>, each written as schema.table
    /// (for example "dbo.visit"). Empty by default. The same schema and table names are used in DuckDB.
    /// </summary>
    public IReadOnlyList<string> Tables { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When true, every table cloned from SQL Server is also exported as a Delta Lake folder under
    /// delta/schema/table. Off by default. <see cref="LocalStore.ExportDelta"/> exports any DuckDB table on demand.
    /// </summary>
    public bool WriteDelta { get; init; }

    /// <summary>
    /// Extra SQL Server predicate per table, used for both the load and the verification count. Keys are "schema.table"
    /// (or a bare table name), values are conditions such as "IsInactive = 0". Empty by default.
    /// </summary>
    public IReadOnlyDictionary<string, string> TableFilters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full path of the DuckDB file.</summary>
    public string DatabasePath {
        get {
            return Path.Combine(OutputDirectory, DatabaseFileName);
        }
    }
}
