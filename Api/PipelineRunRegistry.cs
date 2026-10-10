namespace DataPuddle.Api;

/// <summary>A pipeline run started through the API. The summary appears once the run has finished.</summary>
public sealed class PipelineRunInfo {
    public string Id { get; init; } = "";
    public string Status { get; set; } = "running";
    public DateTime StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; set; }
    public PipelineSummary? Summary { get; set; }
    public string? Error { get; set; }
}

/// <summary>Remembers recent pipeline runs (in memory only) and allows one run at a time.</summary>
public sealed class PipelineRunRegistry {
    private const int KeepRuns = 50;

    private readonly object _lock = new object();
    private readonly List<PipelineRunInfo> _runs = new List<PipelineRunInfo>();

    /// <summary>Records a new run, or returns null when another run is still going.</summary>
    public PipelineRunInfo? TryStart() {
        lock (_lock) {
            if (_runs.Any(r => r.Status == "running")) {
                return null;
            }
            PipelineRunInfo run = new PipelineRunInfo {
                Id = Guid.NewGuid().ToString("N"),
                StartedUtc = DateTime.UtcNow
            };
            _runs.Add(run);
            while (_runs.Count > KeepRuns) {
                _runs.RemoveAt(0);
            }
            return run;
        }
    }

    public void Finish(PipelineRunInfo run, PipelineSummary? summary, string? error) {
        lock (_lock) {
            run.Summary = summary;
            run.Error = error;
            run.FinishedUtc = DateTime.UtcNow;
            run.Status = error == null && summary != null && summary.Status == "success" ? "success" : "failed";
        }
    }

    public PipelineRunInfo? Get(string id) {
        lock (_lock) {
            return _runs.FirstOrDefault(r => r.Id == id);
        }
    }

    /// <summary>Recent runs, newest first.</summary>
    public List<PipelineRunInfo> List() {
        lock (_lock) {
            return _runs.AsEnumerable().Reverse().ToList();
        }
    }
}
