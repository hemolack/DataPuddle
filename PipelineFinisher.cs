using System.Globalization;

namespace DataPuddle;

/// <summary>
/// What happens when a pipeline run ends, however it was started: write the summary file, print the
/// result, and fire the OnSuccess or OnError action.
/// </summary>
public static class PipelineFinisher {
    /// <summary>The summary file location: the configured path, or summary.json in the output folder.</summary>
    public static string ResolveSummaryTarget(string? setting, string outputDirectory) {
        return string.IsNullOrWhiteSpace(setting)
            ? Path.Combine(outputDirectory, "summary.json")
            : Path.GetFullPath(setting);
    }

    /// <summary>The summary's customerId: the CustomerId pipeline parameter, when it is a whole number.</summary>
    public static long? CustomerIdFrom(IReadOnlyDictionary<string, string> parameters) {
        if (parameters.TryGetValue("CustomerId", out string? text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) {
            return value;
        }
        return null;
    }

    /// <summary>
    /// Writes the summary, runs the matching action and returns the exit code: 0 for success, 1 when the
    /// run or its row-count checks failed, 3 when the run was fine but an action failed.
    /// </summary>
    public static int Complete(
        PipelineDefinition pipeline,
        PipelineSummary summary,
        bool pipelineSucceeded,
        bool allMatch,
        string summaryTarget,
        PipelineActionOptions? onSuccess,
        PipelineActionOptions? onError) {

        int exitCode = allMatch && pipelineSucceeded ? 0 : 1;
        string summaryJson = summary.ToJson();
        string? summaryPath = null;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(summaryTarget) ?? Directory.GetCurrentDirectory());
            File.WriteAllText(summaryTarget, summaryJson);
            summaryPath = summaryTarget;
            Console.WriteLine($"Summary: {summaryPath}");
        } catch (Exception ex) {
            Console.Error.WriteLine($"Could not write the summary file {summaryTarget}: {ex.Message}");
        }

        Console.WriteLine($"Pipeline {pipeline.Name}: {summary.Status}" +
            (summary.IgnoredFailures.HasValue ? $" ({summary.IgnoredFailures.Value} failed step(s) ignored)" : ""));
        bool succeeded = pipelineSucceeded && allMatch;
        PipelineActionOptions? action = succeeded ? onSuccess : onError;
        string actionLabel = succeeded ? "OnSuccess" : "OnError";
        if (action != null) {
            bool actionOk = PipelineActionRunner.Execute(actionLabel, action, summary, summaryJson, summaryPath, Console.WriteLine);
            if (!actionOk && exitCode == 0) {
                exitCode = 3;
            }
        }
        return exitCode;
    }
}
