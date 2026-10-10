using System.Security.Cryptography;
using System.Text;

namespace DataPuddle.Api;

/// <summary>What a request wants to do. Every endpoint declares one of these before it touches data.</summary>
public enum ApiAction {
    /// <summary>Read rows, schemas and table lists.</summary>
    Read,

    /// <summary>Insert, update or delete rows; import data; create or drop tables; run scripts.</summary>
    Write,

    /// <summary>Run a caller-supplied SQL statement.</summary>
    Sql,

    /// <summary>Run a named query from the config file (a query that changes data also needs <see cref="Write"/>).</summary>
    Query,

    /// <summary>Clone from SQL Server, export files, run the pipeline, stop the server.</summary>
    Admin
}

/// <summary>The table a request is about, when it is about one.</summary>
public sealed record ApiResource(string? Schema, string? Table, string? Name = null);

/// <summary>Who is calling. Grows (scopes, table lists) when keys get more than one level of access.</summary>
public sealed record ApiPrincipal(string KeyName);

/// <summary>Turns the key a caller presented into a principal, or null when the key is not valid.</summary>
public interface IApiKeyStore {
    ApiPrincipal? Authenticate(string? presentedKey);
}

/// <summary>
/// Decides whether a principal may perform an action. This is the single place access rules live, so
/// per-key scopes and table lists can be added later by replacing the implementation, without touching
/// any endpoint.
/// </summary>
public interface IApiAuthorizer {
    bool IsAllowed(ApiPrincipal principal, ApiAction action, ApiResource? resource);
}

/// <summary>Checks presented keys against the configured keys without revealing where they differ.</summary>
public sealed class StaticKeyStore : IApiKeyStore {
    private readonly List<(string Name, byte[] Hash)> _keys = new List<(string Name, byte[] Hash)>();

    public StaticKeyStore(IEnumerable<ApiKeyDefinition> keys) {
        foreach (ApiKeyDefinition key in keys) {
            _keys.Add((key.Name, Hash(key.Secret)));
        }
    }

    public ApiPrincipal? Authenticate(string? presentedKey) {
        if (string.IsNullOrEmpty(presentedKey)) {
            return null;
        }

        byte[] presented = Hash(presentedKey);
        ApiPrincipal? match = null;
        foreach ((string Name, byte[] Hash) key in _keys) {
            if (CryptographicOperations.FixedTimeEquals(presented, key.Hash)) {
                match = new ApiPrincipal(key.Name);
            }
        }
        return match;
    }

    private static byte[] Hash(string text) {
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }
}

/// <summary>
/// The first-pass rules: every key can do everything, unless the server is read-only, in which case
/// nothing that changes data or files is allowed. SQL is still allowed in read-only mode because the
/// database runs it inside a read-only transaction.
/// </summary>
public sealed class DefaultApiAuthorizer : IApiAuthorizer {
    private readonly bool _readOnly;

    public DefaultApiAuthorizer(bool readOnly) {
        _readOnly = readOnly;
    }

    public bool IsAllowed(ApiPrincipal principal, ApiAction action, ApiResource? resource) {
        switch (action) {
            case ApiAction.Read:
            case ApiAction.Sql:
            case ApiAction.Query:
                return true;
            default:
                return !_readOnly;
        }
    }
}
