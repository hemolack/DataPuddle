using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DataPuddle.Api.Endpoints;

/// <summary>Endpoints about the server itself.</summary>
public static class SystemEndpoints {
    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/", (HttpContext context) => {
            context.Response.Redirect("/swagger");
            return Task.CompletedTask;
        }).ExcludeFromDescription();

        app.MapGet("/health", () => Results.Json(new { status = "ok" }))
            .WithTags("System")
            .WithSummary("Is the server up? Needs no key.");

        app.MapGet("/plugins", (HttpContext context, ApiServices api) => {
            api.Authorize(context, ApiAction.Read);
            DataPuddle.Plugins.PluginHost host = api.Store.Plugins ?? DataPuddle.Plugins.PluginHost.Empty();
            return Results.Json(new {
                plugins = host.Plugins.Select(p => new { p.Name, p.Description, p.Version }),
                functions = host.Functions.Select(f => new {
                    f.Name,
                    f.PluginName,
                    f.Signature,
                    f.Description,
                    f.Registered
                })
            });
        })
            .WithTags("System")
            .WithSummary("Plugins that are loaded and the SQL functions they provide.");

        app.MapGet("/info", (HttpContext context, ApiServices api) => {
            api.Authorize(context, ApiAction.Read);
            return Results.Json(new {
                name = "DataPuddle",
                readOnly = api.Options.ReadOnly,
                maxRows = api.Options.MaxRows,
                queryTimeoutSeconds = api.Options.QueryTimeoutSeconds,
                fileAccessRestricted = !api.Options.AllowFileAccess,
                pipelineConfigured = api.PipelineFile != null,
                serverTimeUtc = DateTime.UtcNow
            });
        })
            .WithTags("System")
            .WithSummary("Server settings the caller may need to know (read-only mode, row limit, and so on).");
    }
}
