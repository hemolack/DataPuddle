using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace DataPuddle.Api.Endpoints;

/// <summary>Whole-program operations: clone from SQL Server, export files, run the pipeline, stop the server.</summary>
public static class OperationEndpoints {
    public static void Map(IEndpointRouteBuilder app) {
        app.MapPost("/clone", Clone)
            .WithTags("Operations")
            .WithSummary("Clone tables from SQL Server into the local copy.")
            .WithDescription("Body: {\"tables\": [\"dbo.invoices\", \"dbo.lineitems\"]}. Waits until the cloning finishes. Replaces any local table of the same name.");

        app.MapPost("/export", Export)
            .WithTags("Operations")
            .WithSummary("Export tables to files on the server.")
            .WithDescription("Body: {\"tables\": [\"dbo.invoices\"], \"format\": \"delta|csv|tsv|delimited\", \"delimiter\": \"|\"}. The delimiter is only for the delimited format.");

        app.MapPost("/pipeline/run", RunPipeline)
            .WithTags("Operations")
            .WithSummary("Start the configured pipeline.")
            .WithDescription("Body (optional): {\"parameters\": {\"CustomerId\": \"1234\"}}. Returns 202 and a run id; poll GET /runs/{id}. Only one run at a time.");

        app.MapGet("/runs", ListRuns)
            .WithTags("Operations")
            .WithSummary("Recent pipeline runs started through the API, newest first.");

        app.MapGet("/runs/{id}", GetRun)
            .WithTags("Operations")
            .WithSummary("Status of one run. When it has finished, includes the same summary the pipeline writes to its summary file.");

        app.MapPost("/shutdown", Shutdown)
            .WithTags("Operations")
            .WithSummary("Stop the server.");
    }

    private static async Task Clone(HttpContext context, ApiServices api) {
        api.Authorize(context, ApiAction.Admin);
        List<string> tables = await ReadTablesAsync(context);

        IReadOnlyList<TableCopyResult> results;
        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Exclusive, context.RequestAborted, context)) {
            results = await Task.Run(() => api.Store.Clone(tables));
        }

        await context.Response.WriteAsJsonAsync(results.Select(r => new {
            table = r.FullName,
            sqlCount = r.SqlCount,
            localCount = r.LocalCount,
            outputCount = r.DeltaCount,
            countsMatch = r.CountsMatch,
            seconds = Math.Round(r.Elapsed.TotalSeconds, 3)
        }), context.RequestAborted);
    }

    private static async Task Export(HttpContext context, ApiServices api) {
        api.Authorize(context, ApiAction.Admin);
        List<string> tables;
        ExportFormat format = ExportFormat.Delta;
        char? delimiter = null;

        using (JsonDocument document = await RequestBody.ReadJsonAsync(context)) {
            tables = TablesFrom(document.RootElement);

            if (document.RootElement.TryGetProperty("format", out JsonElement formatElement) && formatElement.ValueKind == JsonValueKind.String) {
                if (!Enum.TryParse(formatElement.GetString(), true, out format)) {
                    throw ApiException.BadRequest("format must be delta, csv, tsv or delimited.");
                }
            }
            if (document.RootElement.TryGetProperty("delimiter", out JsonElement delimiterElement) && delimiterElement.ValueKind == JsonValueKind.String) {
                string text = delimiterElement.GetString() ?? "";
                if (text.Equals("tab", StringComparison.OrdinalIgnoreCase)) {
                    delimiter = '\t';
                } else if (text.Length == 1) {
                    delimiter = text[0];
                } else {
                    throw ApiException.BadRequest("delimiter must be a single character, or 'tab'.");
                }
            }
        }
        if (format == ExportFormat.Delimited && !delimiter.HasValue) {
            throw ApiException.BadRequest("The delimited format needs a \"delimiter\".");
        }

        List<(string Schema, string Table)> parsed;
        try {
            parsed = LocalStore.ParseTableList(tables);
        } catch (FormatException ex) {
            throw ApiException.BadRequest(ex.Message);
        }

        List<ExportResult> results = new List<ExportResult>();
        using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Exclusive, context.RequestAborted, context)) {
            foreach ((string schema, string table) in parsed) {
                ExportResult result;
                try {
                    result = await Task.Run(() => api.Store.Export(format, schema, table, delimiter));
                } catch (ArgumentException ex) {
                    throw ApiException.BadRequest(ex.Message);
                } catch (NotSupportedException ex) {
                    throw ApiException.BadRequest(ex.Message);
                }
                results.Add(result);
            }
        }

        await context.Response.WriteAsJsonAsync(results.Select(r => new {
            table = r.FullName,
            format = r.Format,
            location = r.Location,
            localCount = r.LocalCount,
            exportedCount = r.ExportedCount,
            countsMatch = r.CountsMatch,
            seconds = Math.Round(r.Elapsed.TotalSeconds, 3)
        }), context.RequestAborted);
    }

    private static async Task<List<string>> ReadTablesAsync(HttpContext context) {
        using (JsonDocument document = await RequestBody.ReadJsonAsync(context)) {
            return TablesFrom(document.RootElement);
        }
    }

    // "tables" may be an array of names or one comma-separated string.
    private static List<string> TablesFrom(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tables", out JsonElement tablesElement)) {
            throw ApiException.BadRequest("The body needs \"tables\", for example {\"tables\": [\"dbo.invoices\"]}.");
        }

        List<string> tables = new List<string>();
        if (tablesElement.ValueKind == JsonValueKind.String) {
            tables.AddRange((tablesElement.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        } else if (tablesElement.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in tablesElement.EnumerateArray()) {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) {
                    throw ApiException.BadRequest("Each entry in \"tables\" must be a schema.table name.");
                }
                tables.Add(item.GetString()!.Trim());
            }
        } else {
            throw ApiException.BadRequest("\"tables\" must be an array of names or a comma-separated string.");
        }

        if (tables.Count == 0) {
            throw ApiException.BadRequest("\"tables\" is empty.");
        }
        try {
            LocalStore.ParseTableList(tables);
        } catch (FormatException ex) {
            throw ApiException.BadRequest(ex.Message);
        }
        return tables;
    }

    // ---------- Pipeline ----------

    private static async Task RunPipeline(HttpContext context, ApiServices api) {
        api.Authorize(context, ApiAction.Admin);
        if (api.PipelineFile == null) {
            throw ApiException.BadRequest("No pipeline is configured. Set Pipeline:File in the config file.");
        }

        Dictionary<string, string> overrides = new Dictionary<string, string>(api.PipelineParameters, StringComparer.OrdinalIgnoreCase);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")) {
            string body = await RequestBody.ReadTextAsync(context);
            if (!string.IsNullOrWhiteSpace(body)) {
                using (JsonDocument document = JsonDocument.Parse(body)) {
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("parameters", out JsonElement parameters)) {
                        if (parameters.ValueKind != JsonValueKind.Object) {
                            throw ApiException.BadRequest("\"parameters\" must be an object of names and values.");
                        }
                        foreach (JsonProperty property in parameters.EnumerateObject()) {
                            overrides[property.Name] = property.Value.ValueKind == JsonValueKind.String
                                ? property.Value.GetString() ?? ""
                                : property.Value.GetRawText();
                        }
                    }
                }
            }
        }

        PipelineDefinition pipeline;
        try {
            pipeline = PipelineDefinition.Load(api.PipelineFile, overrides);
        } catch (PipelineConfigException ex) {
            throw ApiException.BadRequest("Pipeline configuration error: " + ex.Message);
        }

        PipelineRunInfo? run = api.Runs.TryStart();
        if (run == null) {
            throw new ApiException(409, "run_in_progress", "A pipeline run is already in progress. Check GET /runs.");
        }

        // The run continues after this response is sent, so it must not be tied to the request.
        _ = Task.Run(() => ExecuteRun(api, pipeline, run));

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.Headers.Location = "/runs/" + run.Id;
        await context.Response.WriteAsJsonAsync(new { id = run.Id, status = run.Status, statusUrl = "/runs/" + run.Id }, context.RequestAborted);
    }

    private static async Task ExecuteRun(ApiServices api, PipelineDefinition pipeline, PipelineRunInfo run) {
        PipelineSummary summary = new PipelineSummary {
            Pipeline = pipeline.Name,
            StartedUtc = DateTime.UtcNow,
            Environment = api.EnvironmentName,
            ConfigFile = api.ConfigFile,
            PipelineFile = pipeline.FilePath,
            Database = api.Store.DatabasePath,
            CustomerId = PipelineFinisher.CustomerIdFrom(pipeline.Parameters),
            Parameters = new Dictionary<string, string>(pipeline.Parameters)
        };

        try {
            bool succeeded;
            Stopwatch watch = Stopwatch.StartNew();
            using (DatabaseSession session = await api.Gate.EnterAsync(AccessMode.Exclusive, CancellationToken.None)) {
                PipelineRunner runner = new PipelineRunner(api.Store, Console.WriteLine);
                succeeded = runner.Run(pipeline, summary);
            }
            watch.Stop();

            summary.Status = succeeded ? "success" : "failed";
            summary.FinishedUtc = DateTime.UtcNow;
            summary.DurationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3);

            string target = api.SummaryTarget ?? PipelineFinisher.ResolveSummaryTarget(null, api.OutputDirectory);
            PipelineFinisher.Complete(pipeline, summary, succeeded, true, target, api.OnSuccess, api.OnError);
            api.Runs.Finish(run, summary, null);
        } catch (Exception ex) {
            Console.Error.WriteLine("Pipeline run failed: " + ex.Message);
            api.Runs.Finish(run, null, ex.Message);
        }
    }

    private static async Task ListRuns(HttpContext context, ApiServices api) {
        api.Authorize(context, ApiAction.Admin);
        await context.Response.WriteAsJsonAsync(api.Runs.List(), context.RequestAborted);
    }

    private static async Task GetRun(HttpContext context, ApiServices api, string id) {
        api.Authorize(context, ApiAction.Admin);
        PipelineRunInfo? run = api.Runs.Get(id);
        if (run == null) {
            throw ApiException.NotFound("There is no run with that id (runs are only remembered until the server restarts).");
        }
        await context.Response.WriteAsJsonAsync(run, context.RequestAborted);
    }

    private static async Task Shutdown(HttpContext context, ApiServices api, IHostApplicationLifetime lifetime) {
        api.Authorize(context, ApiAction.Admin);
        await context.Response.WriteAsJsonAsync(new { stopping = true }, context.RequestAborted);
        lifetime.StopApplication();
    }
}
