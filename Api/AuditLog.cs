using System.Text.Json;

namespace DataPuddle.Api;

/// <summary>One line of the audit log: who called what and what happened. Never holds SQL text or request bodies.</summary>
public sealed record AuditEntry(
    DateTime TimestampUtc,
    string Key,
    string Method,
    string Path,
    int Status,
    long Milliseconds,
    long? Rows,
    string? SqlHash);

/// <summary>Appends one JSON object per line to a file. Does nothing when no path is configured.</summary>
public sealed class AuditLog {
    private readonly string? _path;
    private readonly object _lock = new object();

    public AuditLog(string? path) {
        _path = path;
        if (_path != null) {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }
        }
    }

    public string? FilePath {
        get {
            return _path;
        }
    }

    public void Write(AuditEntry entry) {
        if (_path == null) {
            return;
        }

        string line = JsonSerializer.Serialize(new {
            time = entry.TimestampUtc.ToString("O"),
            key = entry.Key,
            method = entry.Method,
            path = entry.Path,
            status = entry.Status,
            ms = entry.Milliseconds,
            rows = entry.Rows,
            sqlHash = entry.SqlHash
        });
        try {
            lock (_lock) {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        } catch (IOException ex) {
            Console.Error.WriteLine("Could not write the audit log: " + ex.Message);
        }
    }
}
