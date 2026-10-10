using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataPuddle;

/// <summary>
/// The record of one run, written to the summary JSON file and sent as the body of a webhook.
/// Property names are written in camelCase.
/// </summary>
public sealed class PipelineSummary {
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The pipeline's name.</summary>
    public string Pipeline { get; set; } = "";

    /// <summary>"success" or "failed".</summary>
    public string Status { get; set; } = "failed";

    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public double DurationSeconds { get; set; }

    public string Environment { get; set; } = "";
    public string? ConfigFile { get; set; }
    public string PipelineFile { get; set; } = "";
    public string Database { get; set; } = "";
    public long? CustomerId { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();

    /// <summary>Tables cloned from the startup "Tables" setting before the steps ran.</summary>
    public List<ResultSummary> StartupClones { get; set; } = new List<ResultSummary>();

    public List<StepSummary> Steps { get; set; } = new List<StepSummary>();

    /// <summary>
    /// How many steps failed but were allowed to continue ("continueOnError"). Left out when there were none.
    /// A run that only has such failures still ends with status "success".
    /// </summary>
    public int? IgnoredFailures { get; set; }

    /// <summary>Set when the run failed: which step, and why.</summary>
    public PipelineError? Error { get; set; }

    public string ToJson() {
        return JsonSerializer.Serialize(this, JsonOptions);
    }
}

public sealed class StepSummary {
    public int Index { get; set; }
    public string Name { get; set; } = "";

    /// <summary>sql, clone, export or assert.</summary>
    public string Kind { get; set; } = "";

    /// <summary>success, failed or skipped.</summary>
    public string Status { get; set; } = "";

    public double DurationSeconds { get; set; }
    public string? Detail { get; set; }
    public string? Error { get; set; }

    /// <summary>True when this step failed and the run carried on because of "continueOnError".</summary>
    public bool? ContinuedAfterFailure { get; set; }

    public List<ResultSummary>? Results { get; set; }
}

/// <summary>Row counts for one cloned or exported table.</summary>
public sealed class ResultSummary {
    public string Table { get; set; } = "";

    /// <summary>Rows in SQL Server (clone only).</summary>
    public long? SqlCount { get; set; }

    /// <summary>Rows in the local table.</summary>
    public long LocalCount { get; set; }

    /// <summary>Rows in the Delta copy (clone) or the exported file or Delta table (export).</summary>
    public long? OutputCount { get; set; }

    /// <summary>Where an export was written.</summary>
    public string? Location { get; set; }

    public bool CountsMatch { get; set; }
}

public sealed class PipelineError {
    public string Step { get; set; } = "";
    public string Message { get; set; } = "";
}
