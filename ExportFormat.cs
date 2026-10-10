namespace DataPuddle;

/// <summary>Output formats for exporting a DuckDB table.</summary>
public enum ExportFormat {
    /// <summary>A Delta Lake folder (Parquet data files plus a _delta_log).</summary>
    Delta,

    /// <summary>Comma-separated text with a header row.</summary>
    Csv,

    /// <summary>Tab-separated text with a header row.</summary>
    Tsv,

    /// <summary>Delimited text with a header row, using a delimiter character the caller supplies.</summary>
    Delimited
}
