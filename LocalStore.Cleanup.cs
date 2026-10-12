namespace DataPuddle;

public sealed partial class LocalStore {
    /// <summary>
    /// Deletes the DuckDB file (and its write-ahead log and spill folder) and every Delta table written
    /// under the output folder's delta/ directory. Exports in other formats (CSV and so on) are left alone.
    /// The connection must be closed first. Returns the paths that could not be deleted; empty means all went.
    /// </summary>
    public IReadOnlyList<string> DeleteLocalData() {
        if (_duck != null) {
            throw new InvalidOperationException("Dispose the store before deleting its files.");
        }

        List<string> failures = new List<string>();
        foreach (string file in new[] { DatabasePath, DatabasePath + ".wal" }) {
            try {
                if (File.Exists(file)) {
                    File.Delete(file);
                }
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                failures.Add(file);
            }
        }

        foreach (string directory in new[] { DatabasePath + ".tmp", Path.Combine(_options.OutputDirectory, "delta") }) {
            try {
                if (Directory.Exists(directory)) {
                    Directory.Delete(directory, recursive: true);
                }
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                failures.Add(directory);
            }
        }
        return failures;
    }
}
