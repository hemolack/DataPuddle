using System.Security.Cryptography;
using System.Text.Json;

namespace DataPuddle.Deidentify;

/// <summary>
/// The Pseudonymize action: replaces an identifier with a random surrogate and remembers the pair so the same
/// identifier always gets the same surrogate. Surrogates are random, never a hash or encryption of the original,
/// because 45 CFR 164.514(c) does not allow a re-identification code to be derived from information about the
/// individual.
///
/// The map is kept in memory. When a file is configured every new pair is also appended to it, and the file is
/// read back at startup so surrogates stay the same from one run to the next. That file links surrogates to real
/// identifiers, so it is PHI: keep it out of the de-identified data set and restrict who can read it.
/// </summary>
internal sealed class PseudonymMap {
    private readonly object _lock = new object();
    private readonly Dictionary<(string Domain, string Value), string> _map = new Dictionary<(string Domain, string Value), string>();
    private readonly HashSet<string> _used = new HashSet<string>(StringComparer.Ordinal);
    private readonly StreamWriter? _writer;

    /// <summary>Creates the map. A null <paramref name="file"/> keeps it in memory only.</summary>
    public PseudonymMap(string? file) {
        if (file == null) {
            return;
        }

        if (File.Exists(file)) {
            Load(file);
        }

        FileStreamOptions options = new FileStreamOptions {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read
        };
        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        _writer = new StreamWriter(new FileStream(file, options)) {
            AutoFlush = true
        };
    }

    public int Count {
        get {
            lock (_lock) {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// The surrogate for <paramref name="value"/>. <paramref name="domain"/> names the kind of identifier ("mrn",
    /// "customer_id"); the same value in the same domain always gets the same surrogate, so use one domain for every
    /// column that holds the same identifier.
    /// </summary>
    public string? Pseudonymize(string? value, string? domain) {
        if (value == null) {
            return null;
        }
        if (string.IsNullOrWhiteSpace(domain)) {
            throw new ArgumentException("deid_pseudonym needs a domain such as 'mrn' as its second argument.");
        }

        lock (_lock) {
            (string, string) key = (domain, value);
            if (_map.TryGetValue(key, out string? existing)) {
                return existing;
            }

            string surrogate = NewSurrogate();
            if (_writer != null) {
                // Write first: a surrogate that was not saved must not be handed out.
                _writer.WriteLine(JsonSerializer.Serialize(new string[] { domain, value, surrogate }));
            }
            _map[key] = surrogate;
            _used.Add(surrogate);
            return surrogate;
        }
    }

    private string NewSurrogate() {
        while (true) {
            string candidate = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            if (!_used.Contains(candidate)) {
                return candidate;
            }
        }
    }

    private void Load(string file) {
        int lineNumber = 0;
        foreach (string line in File.ReadLines(file)) {
            lineNumber++;
            if (line.Length == 0) {
                continue;
            }

            string[]? parts;
            try {
                parts = JsonSerializer.Deserialize<string[]>(line);
            } catch (JsonException) {
                parts = null;
            }

            // A damaged file stops the plugin. Carrying on would give already-mapped identifiers a second surrogate.
            if (parts == null || parts.Length != 3) {
                throw new InvalidDataException($"{file} line {lineNumber} is not a valid mapping entry.");
            }
            _map[(parts[0], parts[1])] = parts[2];
            _used.Add(parts[2]);
        }
    }
}
