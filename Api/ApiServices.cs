using Microsoft.AspNetCore.Http;

namespace DataPuddle.Api;

/// <summary>Everything the endpoints share, created once when the server starts.</summary>
public sealed class ApiServices {
    public ApiServices(
        LocalStore store,
        DatabaseGate gate,
        ApiOptions options,
        IApiKeyStore keys,
        IApiAuthorizer authorizer,
        AuditLog audit,
        PipelineRunRegistry runs) {
        Store = store;
        Gate = gate;
        Options = options;
        Keys = keys;
        Authorizer = authorizer;
        Audit = audit;
        Runs = runs;
    }

    public LocalStore Store { get; }
    public DatabaseGate Gate { get; }
    public ApiOptions Options { get; }
    public IApiKeyStore Keys { get; }
    public IApiAuthorizer Authorizer { get; }
    public AuditLog Audit { get; }
    public PipelineRunRegistry Runs { get; }

    /// <summary>The pipeline file named in the config, or null when there is none.</summary>
    public string? PipelineFile { get; init; }

    /// <summary>Pipeline parameters given on the command line; a run request can override them.</summary>
    public IReadOnlyDictionary<string, string> PipelineParameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Where a pipeline run started through the API writes its summary file.</summary>
    public string? SummaryTarget { get; init; }

    /// <summary>Actions fired when a pipeline run started through the API succeeds or fails.</summary>
    public PipelineActionOptions? OnSuccess { get; init; }
    public PipelineActionOptions? OnError { get; init; }

    public string EnvironmentName { get; init; } = "";
    public string? ConfigFile { get; init; }

    public string OutputDirectory {
        get {
            return Store.Options.OutputDirectory;
        }
    }

    public const string PrincipalItem = "dp.principal";

    /// <summary>The caller, as set by the authentication step. Throws 401 if there is none.</summary>
    public static ApiPrincipal GetPrincipal(HttpContext context) {
        if (context.Items.TryGetValue(PrincipalItem, out object? value) && value is ApiPrincipal principal) {
            return principal;
        }
        throw ApiException.Unauthorized();
    }

    /// <summary>
    /// Every endpoint calls this first, naming what it is about to do. All access rules live in the
    /// <see cref="IApiAuthorizer"/>, so adding scopes later changes that class, not the endpoints.
    /// </summary>
    public void Authorize(HttpContext context, ApiAction action, string? schema = null, string? table = null, string? name = null) {
        ApiPrincipal principal = GetPrincipal(context);
        ApiResource? resource = table == null && name == null ? null : new ApiResource(schema, table, name);
        if (!Authorizer.IsAllowed(principal, action, resource)) {
            string reason = Options.ReadOnly
                ? "The server is running in read-only mode."
                : "This key is not allowed to do that.";
            throw ApiException.Forbidden(reason);
        }
    }
}
