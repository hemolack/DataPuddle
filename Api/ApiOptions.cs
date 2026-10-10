using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace DataPuddle.Api;

/// <summary>A problem in the Api section of the config file.</summary>
public sealed class ApiConfigException : Exception {
    public ApiConfigException(string message) : base(message) {
    }
}

/// <summary>
/// A key that may call the API. For now there is one key with full access. When different callers need
/// different rights, this is where scopes and table lists go (see <see cref="IApiAuthorizer"/>).
/// </summary>
public sealed class ApiKeyDefinition {
    public string Name { get; init; } = "default";
    public string Secret { get; init; } = "";
}

/// <summary>Settings for the REST API, read from the Api section of the config file.</summary>
public sealed class ApiOptions {
    private const int MinimumKeyLength = 16;

    /// <summary>Address to listen on. The default only accepts connections from this computer.</summary>
    public string Listen { get; init; } = "http://127.0.0.1:5080";

    /// <summary>When true, every request that could change data or files is refused.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Most rows any one response returns.</summary>
    public int MaxRows { get; init; } = 10000;

    /// <summary>Longest a single query may run before it is cancelled.</summary>
    public int QueryTimeoutSeconds { get; init; } = 60;

    /// <summary>Longest a request waits for the database when another request is using it.</summary>
    public int LockWaitSeconds { get; init; } = 30;

    public long MaxUploadBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// When false (the default), SQL sent to the API cannot read or write files, attach other databases
    /// or load extensions, except inside the output folder and <see cref="AllowedDirectories"/>.
    /// </summary>
    public bool AllowFileAccess { get; init; }

    /// <summary>Extra folders SQL may read from and write to (full paths).</summary>
    public IReadOnlyList<string> AllowedDirectories { get; init; } = new List<string>();

    /// <summary>Where the audit log is written, or null for no audit log.</summary>
    public string? AuditLogPath { get; init; }

    /// <summary>Queries callers can run by name (the Api:Queries section).</summary>
    public IReadOnlyDictionary<string, NamedQuery> Queries { get; init; } = new Dictionary<string, NamedQuery>();

    public IReadOnlyList<ApiKeyDefinition> Keys { get; init; } = new List<ApiKeyDefinition>();

    /// <summary>True when no key was configured and one was made up for this run.</summary>
    public bool KeyWasGenerated { get; init; }

    public static ApiOptions Load(IConfigurationSection section, string defaultAuditLogPath, string configDirectory, string? listenOverride, bool readOnlyOverride) {
        string listen = !string.IsNullOrWhiteSpace(listenOverride) ? listenOverride : (section["Listen"] ?? "http://127.0.0.1:5080");
        if (!Uri.TryCreate(listen, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
            throw new ApiConfigException($"Api:Listen must be an absolute http or https address such as http://127.0.0.1:5080, but was '{listen}'.");
        }

        bool readOnly = readOnlyOverride || ReadBool(section, "ReadOnly", false);

        string? secret = section["Key"];
        bool generated = false;
        if (string.IsNullOrWhiteSpace(secret)) {
            secret = GenerateKey();
            generated = true;
        } else if (secret.Length < MinimumKeyLength) {
            throw new ApiConfigException($"Api:Key must be at least {MinimumKeyLength} characters long.");
        }

        List<string> directories = new List<string>();
        foreach (IConfigurationSection child in section.GetSection("AllowedDirectories").GetChildren()) {
            if (!string.IsNullOrWhiteSpace(child.Value)) {
                directories.Add(Path.GetFullPath(child.Value));
            }
        }

        string? auditSetting = section["AuditLog"];
        string? auditPath = auditSetting == null
            ? defaultAuditLogPath
            : (string.IsNullOrWhiteSpace(auditSetting) ? null : Path.GetFullPath(auditSetting));

        return new ApiOptions {
            Listen = listen,
            ReadOnly = readOnly,
            MaxRows = ReadInt(section, "MaxRows", 10000, 1),
            QueryTimeoutSeconds = ReadInt(section, "QueryTimeoutSeconds", 60, 1),
            LockWaitSeconds = ReadInt(section, "LockWaitSeconds", 30, 1),
            MaxUploadBytes = ReadInt(section, "MaxUploadMegabytes", 512, 1) * 1024L * 1024L,
            AllowFileAccess = ReadBool(section, "AllowFileAccess", false),
            AllowedDirectories = directories,
            AuditLogPath = auditPath,
            Queries = NamedQueryLoader.Load(section.GetSection("Queries"), configDirectory),
            Keys = new List<ApiKeyDefinition> { new ApiKeyDefinition { Name = "default", Secret = secret } },
            KeyWasGenerated = generated
        };
    }

    private static string GenerateKey() {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static int ReadInt(IConfigurationSection section, string name, int defaultValue, int minimum) {
        string? text = section[name];
        if (string.IsNullOrWhiteSpace(text)) {
            return defaultValue;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < minimum) {
            throw new ApiConfigException($"Api:{name} must be a whole number, {minimum} or more.");
        }
        return value;
    }

    private static bool ReadBool(IConfigurationSection section, string name, bool defaultValue) {
        string? text = section[name];
        if (string.IsNullOrWhiteSpace(text)) {
            return defaultValue;
        }
        if (!bool.TryParse(text, out bool value)) {
            throw new ApiConfigException($"Api:{name} must be true or false.");
        }
        return value;
    }
}
