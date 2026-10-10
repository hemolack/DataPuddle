using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DataPuddle.Api.Endpoints;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

namespace DataPuddle.Api;

/// <summary>Builds and runs the REST API on top of an open <see cref="LocalStore"/>.</summary>
public static class ApiServer
{
    private const string SqlHashItem = "dp.sqlHash";

    /// <summary>Records a short fingerprint of a SQL statement for the audit log (never the SQL itself).</summary>
    public static void NoteSql(HttpContext context, string sql)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql));
        context.Items[SqlHashItem] = Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// Starts listening and blocks until the server is stopped (Ctrl+C, or POST /shutdown).
    /// <paramref name="onStarted"/> runs once the server is accepting requests.
    /// </summary>
    public static void Run(ApiServices services, Action? onStarted)
    {
        ApiOptions options = services.Options;

        if (!options.AllowFileAccess)
        {
            services.Store.RestrictFileAccess(options.AllowedDirectories);
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(options.Listen);
        builder.WebHost.ConfigureKestrel(kestrel => {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = options.MaxUploadBytes;
        });

        builder.Services.AddSingleton(services);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(swagger => {
            swagger.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "DataPuddle API",
                Version = "v1",
                Description = "Read and write the local copy of the data. Send your key as the X-Api-Key header."
            });
            swagger.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Api-Key",
                Description = "The API key printed when the server started, or set as Api:Key."
            });
            swagger.AddSecurityRequirement(new OpenApiSecurityRequirement {
                {
                    new OpenApiSecurityScheme {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
                    },
                    new List<string>()
                }
            });
        });

        WebApplication app = builder.Build();

        app.UseSwagger();
        app.UseSwaggerUI(ui => {
            ui.SwaggerEndpoint("/swagger/v1/swagger.json", "DataPuddle API v1");
        });

        app.Use(async (HttpContext context, RequestDelegate next) => await HandleErrorsAndAuditAsync(context, next, services));
        app.Use(async (HttpContext context, RequestDelegate next) => await AuthenticateAsync(context, next, services));

        SystemEndpoints.Map(app);
        TableEndpoints.Map(app);
        SqlEndpoints.Map(app);
        QueryEndpoints.Map(app);
        OperationEndpoints.Map(app);

        IHostApplicationLifetime lifetime = app.Lifetime;
        lifetime.ApplicationStarted.Register(() => {
            Console.WriteLine("API is accepting requests.");
            if (onStarted != null)
            {
                try
                {
                    onStarted();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Startup actions failed: " + ex.Message);
                }
            }
        });

        // Printed before the server starts, so the key is visible even if the server then fails to start.
        PrintBanner(services);
        app.Run();
    }

    private static void PrintBanner(ApiServices services)
    {
        ApiOptions options = services.Options;
        Console.WriteLine();
        Console.WriteLine($"Starting the API on {options.Listen}  (docs at {options.Listen.TrimEnd('/')}/swagger)");
        Console.WriteLine(options.ReadOnly
            ? "Mode: read-only (data and files cannot be changed through the API)."
            : "Mode: read and write.");
        if (options.Queries.Count > 0)
        {
            Console.WriteLine($"Named queries: {options.Queries.Count} (GET /queries)");
        }
        if (options.KeyWasGenerated)
        {
            Console.WriteLine("No Api:Key is set, so a key was made up for this run:");
            Console.WriteLine("  " + options.Keys[0].Secret);
            Console.WriteLine("Set Api:Key (or the Api__Key environment variable) to use the same key every time.");
        }
        if (options.AllowFileAccess)
        {
            Console.WriteLine("WARNING: Api:AllowFileAccess is on, so SQL sent to the API can read and write any file this program can.");
        }
        if (services.Audit.FilePath != null)
        {
            Console.WriteLine("Audit log: " + services.Audit.FilePath);
        }
        if (Uri.TryCreate(options.Listen, UriKind.Absolute, out Uri? uri) && !IsLoopback(uri.Host) && uri.Scheme == Uri.UriSchemeHttp)
        {
            Console.WriteLine("WARNING: this address is reachable from other computers over plain http, so the key travels unencrypted. " +
                "Use https, or a loopback address (127.0.0.1) behind a reverse proxy.");
        }
        Console.WriteLine("Press Ctrl+C to stop.");
    }

    private static bool IsLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private static bool IsOpenPath(PathString path)
    {
        return path == "/" || path == "/health" || path.StartsWithSegments("/swagger");
    }

    private static async Task AuthenticateAsync(HttpContext context, RequestDelegate next, ApiServices services)
    {
        if (IsOpenPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        string? presented = context.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            string authorization = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                presented = authorization.Substring(prefix.Length).Trim();
            }
        }

        ApiPrincipal? principal = services.Keys.Authenticate(presented);
        if (principal == null)
        {
            throw ApiException.Unauthorized();
        }

        context.Items[ApiServices.PrincipalItem] = principal;
        await next(context);
    }

    private static async Task HandleErrorsAndAuditAsync(HttpContext context, RequestDelegate next, ApiServices services)
    {
        bool audited = !IsOpenPath(context.Request.Path);
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            await WriteErrorAsync(context, ex.Status, ex.Code, ex.Message);
        }
        catch (DuckDBException ex)
        {
            if (context.Items.ContainsKey(QueryTimer.TimedOutItem))
            {
                await WriteErrorAsync(context, 408, "query_timeout",
                    $"The query ran longer than {services.Options.QueryTimeoutSeconds} seconds and was cancelled.");
            }
            else
            {
                await WriteErrorAsync(context, 400, "sql_error", ex.Message);
            }
        }
        catch (SqlException ex)
        {
            await WriteErrorAsync(context, 502, "sql_server_error", "SQL Server reported an error: " + ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            await WriteErrorAsync(context, ex.StatusCode, "bad_request", ex.Message);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(context, 400, "invalid_json", "The request body is not valid JSON: " + ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unhandled error in {context.Request.Method} {context.Request.Path}: {ex}");
            await WriteErrorAsync(context, 500, "internal_error", "Something went wrong. The details are in the server's console.");
        }
        finally
        {
            watch.Stop();
            if (audited)
            {
                string key = context.Items.TryGetValue(ApiServices.PrincipalItem, out object? principal) && principal is ApiPrincipal p
                    ? p.KeyName
                    : "-";
                long? rows = context.Items.TryGetValue(ResultWriter.RowsItem, out object? rowValue) && rowValue is long count ? count : null;
                string? sqlHash = context.Items.TryGetValue(SqlHashItem, out object? hashValue) ? hashValue as string : null;
                services.Audit.Write(new AuditEntry(
                    DateTime.UtcNow, key, context.Request.Method, context.Request.Path.ToString(),
                    context.Response.StatusCode, watch.ElapsedMilliseconds, rows, sqlHash));
            }
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            // Part of a streamed result has already gone out, so the only honest signal left is to drop the connection.
            context.Abort();
            return;
        }
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        string body = JsonSerializer.Serialize(new { error = new { code, message } });
        await context.Response.WriteAsync(body);
    }
}