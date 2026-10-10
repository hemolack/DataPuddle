using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

namespace DataPuddle;

/// <summary>
/// Runs the steps of a pipeline, in order, against an open <see cref="LocalStore"/>. The first failing
/// step stops the run; the remaining steps are recorded as skipped.
/// </summary>
public sealed class PipelineRunner {
    private const int SampleRowCount = 5;

    private readonly LocalStore _store;
    private readonly Action<string> _log;

    public PipelineRunner(LocalStore store, Action<string> log) {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Runs every step and adds a <see cref="StepSummary"/> for each to <paramref name="summary"/>.
    /// Returns true when the run finished: every step succeeded, or the ones that failed have continueOnError set
    /// (see <see cref="PipelineSummary.IgnoredFailures"/>). On failure <see cref="PipelineSummary.Error"/> is set.
    /// </summary>
    public bool Run(PipelineDefinition pipeline, PipelineSummary summary) {
        int total = pipeline.Steps.Count;
        _log("");
        _log($"== pipeline {pipeline.Name} ({total} step(s)) ==");

        for (int i = 0; i < total; i++) {
            PipelineStep step = pipeline.Steps[i];
            StepSummary stepSummary = new StepSummary {
                Index = step.Index,
                Name = step.Name,
                Kind = step.KindName,
                Status = "failed"
            };
            summary.Steps.Add(stepSummary);
            _log($"[{i + 1}/{total}] {step.Name} ({step.Describe()})");

            Stopwatch watch = Stopwatch.StartNew();
            try {
                RunStep(step, stepSummary);
                watch.Stop();
                stepSummary.Status = "success";
                stepSummary.DurationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3);
                _log($"  done in {LocalStore.FormatElapsed(watch.Elapsed)}");
            } catch (Exception ex) {
                watch.Stop();
                stepSummary.Status = "failed";
                stepSummary.DurationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3);
                stepSummary.Error = ex.Message;

                if (step.ContinueOnError) {
                    stepSummary.ContinuedAfterFailure = true;
                    summary.IgnoredFailures = (summary.IgnoredFailures ?? 0) + 1;
                    Console.Error.WriteLine($"  FAILED (continuing, continueOnError is set): {ex.Message}");
                    continue;
                }

                summary.Error = new PipelineError { Step = step.Name, Message = ex.Message };
                Console.Error.WriteLine($"  FAILED: {ex.Message}");
                AddSkipped(pipeline, summary, i + 1);
                return false;
            }
        }
        return true;
    }

    /// <summary>Records every step from <paramref name="fromIndex"/> (0-based) onward as skipped.</summary>
    public static void AddSkipped(PipelineDefinition pipeline, PipelineSummary summary, int fromIndex) {
        for (int i = fromIndex; i < pipeline.Steps.Count; i++) {
            PipelineStep step = pipeline.Steps[i];
            summary.Steps.Add(new StepSummary {
                Index = step.Index,
                Name = step.Name,
                Kind = step.KindName,
                Status = "skipped"
            });
        }
    }

    private void RunStep(PipelineStep step, StepSummary stepSummary) {
        switch (step.Kind) {
            case PipelineStepKind.Sql:
                RunSql(step, stepSummary);
                break;
            case PipelineStepKind.Clone:
                RunClone(step, stepSummary);
                break;
            case PipelineStepKind.Export:
                RunExport(step, stepSummary);
                break;
            case PipelineStepKind.Assert:
                RunAssert(step, stepSummary);
                break;
            default:
                throw new NotSupportedException(step.Kind.ToString());
        }
    }

    private void RunSql(PipelineStep step, StepSummary stepSummary) {
        int count = step.Statements.Count;
        for (int i = 0; i < count; i++) {
            string statement = step.Statements[i];
            try {
                string translated;
                string? schemaToCreate;
                if (SelectIntoTranslator.TryTranslate(statement, out translated, out schemaToCreate)) {
                    if (schemaToCreate != null) {
                        _store.Execute("CREATE SCHEMA IF NOT EXISTS \"" + schemaToCreate.Replace("\"", "\"\"") + "\"");
                    }
                    statement = translated;
                }
                _store.Execute(statement);
            } catch (Exception ex) {
                throw new InvalidOperationException(
                    $"statement {i + 1} of {count} failed: {ex.Message} -- {Snippet(step.Statements[i])}");
            }
        }
        stepSummary.Detail = $"{count} statement(s)";
    }

    private void RunClone(PipelineStep step, StepSummary stepSummary) {
        IReadOnlyList<TableCopyResult> results = _store.Clone(step.Tables);
        stepSummary.Results = new List<ResultSummary>();
        List<string> mismatched = new List<string>();
        foreach (TableCopyResult result in results) {
            stepSummary.Results.Add(ToResultSummary(result));
            if (!result.CountsMatch) {
                mismatched.Add(result.FullName);
            }
        }
        if (mismatched.Count > 0) {
            throw new InvalidOperationException("row counts do not match for " + string.Join(", ", mismatched));
        }
        stepSummary.Detail = $"{results.Count} table(s) cloned";
    }

    private void RunExport(PipelineStep step, StepSummary stepSummary) {
        stepSummary.Results = new List<ResultSummary>();
        List<string> mismatched = new List<string>();
        foreach (string qualifiedName in step.Tables) {
            (string Schema, string Table) name = LocalStore.ParseTableName(qualifiedName);
            ExportResult result = _store.Export(step.ExportFormat, name.Schema, name.Table, step.Delimiter);
            stepSummary.Results.Add(new ResultSummary {
                Table = result.FullName,
                LocalCount = result.LocalCount,
                OutputCount = result.ExportedCount,
                Location = result.Location,
                CountsMatch = result.CountsMatch
            });
            if (!result.CountsMatch) {
                mismatched.Add(result.FullName);
            }
        }
        if (mismatched.Count > 0) {
            throw new InvalidOperationException("exported row counts do not match for " + string.Join(", ", mismatched));
        }
        stepSummary.Detail = $"{step.Tables.Count} table(s) exported";
    }

    private void RunAssert(PipelineStep step, StepSummary stepSummary) {
        long rowCount = 0;
        List<string> samples = new List<string>();
        using (DuckDB.NET.Data.DuckDBCommand command = _store.Connection.CreateCommand()) {
            command.CommandText = step.AssertQuery;
            using (DbDataReader reader = command.ExecuteReader()) {
                while (reader.Read()) {
                    rowCount++;
                    if (samples.Count < SampleRowCount) {
                        samples.Add(FormatRow(reader));
                    }
                }
            }
        }

        if (rowCount != step.ExpectRows) {
            string message = $"assertion returned {rowCount:N0} row(s), expected {step.ExpectRows:N0}";
            if (samples.Count > 0) {
                message += ". First rows: " + string.Join(" | ", samples);
            }
            throw new InvalidOperationException(message);
        }
        stepSummary.Detail = $"returned {rowCount:N0} row(s) as expected";
    }

    // ---------- Helpers ----------

    private static ResultSummary ToResultSummary(TableCopyResult result) {
        return new ResultSummary {
            Table = result.FullName,
            SqlCount = result.SqlCount,
            LocalCount = result.LocalCount,
            OutputCount = result.DeltaCount,
            CountsMatch = result.CountsMatch
        };
    }

    private static string FormatRow(DbDataReader reader) {
        List<string> cells = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++) {
            string text = reader.IsDBNull(i)
                ? "NULL"
                : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            cells.Add(reader.GetName(i) + "=" + text);
        }
        return string.Join(", ", cells);
    }

    private static string Snippet(string statement) {
        string oneLine = string.Join(" ", statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 120 ? oneLine : oneLine.Substring(0, 117) + "...";
    }
}
