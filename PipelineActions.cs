using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace DataPuddle;

/// <summary>Posts the run summary (JSON) to a URL.</summary>
public sealed class WebhookAction {
    public string Url { get; init; } = "";
    public string Method { get; init; } = "POST";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Starts another program. Arguments may use ${summary}, ${status} and ${pipeline}.</summary>
public sealed class RunAction {
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = new List<string>();
    public string? WorkingDirectory { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
}

/// <summary>
/// What to do when a pipeline run ends: call a webhook, start a program, or both (webhook first).
/// Read from the OnSuccess and OnError sections of the config file.
/// </summary>
public sealed class PipelineActionOptions {
    public WebhookAction? Webhook { get; init; }
    public RunAction? Run { get; init; }

    /// <summary>Reads an action section. Returns null when the section is absent or empty.</summary>
    public static PipelineActionOptions? Load(IConfigurationSection section, string label) {
        if (!section.Exists()) {
            return null;
        }

        WebhookAction? webhook = null;
        IConfigurationSection webhookSection = section.GetSection("Webhook");
        if (webhookSection.Exists()) {
            string? url = webhookSection["Url"];
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
                throw new PipelineConfigException($"{label}:Webhook:Url must be an absolute http or https URL.");
            }

            string method = (webhookSection["Method"] ?? "POST").Trim().ToUpperInvariant();
            if (method != "POST" && method != "PUT" && method != "PATCH") {
                throw new PipelineConfigException($"{label}:Webhook:Method must be POST, PUT or PATCH, but was '{method}'.");
            }

            Dictionary<string, string> headers = new Dictionary<string, string>();
            foreach (IConfigurationSection header in webhookSection.GetSection("Headers").GetChildren()) {
                headers[header.Key] = header.Value ?? "";
            }

            webhook = new WebhookAction {
                Url = url,
                Method = method,
                Headers = headers,
                TimeoutSeconds = ReadTimeout(webhookSection, $"{label}:Webhook", 30)
            };
        }

        RunAction? run = null;
        IConfigurationSection runSection = section.GetSection("Run");
        if (runSection.Exists()) {
            string? command = runSection["Command"];
            if (string.IsNullOrWhiteSpace(command)) {
                throw new PipelineConfigException($"{label}:Run:Command is required.");
            }

            List<string> arguments = new List<string>();
            foreach (IConfigurationSection argument in runSection.GetSection("Arguments").GetChildren()) {
                arguments.Add(argument.Value ?? "");
            }

            run = new RunAction {
                Command = command,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(runSection["WorkingDirectory"]) ? null : runSection["WorkingDirectory"],
                TimeoutSeconds = ReadTimeout(runSection, $"{label}:Run", 300)
            };
        }

        if (webhook == null && run == null) {
            throw new PipelineConfigException($"{label} must contain a Webhook, a Run, or both.");
        }
        return new PipelineActionOptions { Webhook = webhook, Run = run };
    }

    /// <summary>A one-line description for the plan output. The URL's path and query are left out.</summary>
    public string Describe() {
        List<string> parts = new List<string>();
        if (Webhook != null) {
            Uri.TryCreate(Webhook.Url, UriKind.Absolute, out Uri? uri);
            parts.Add($"webhook {Webhook.Method} to {uri?.Host}");
        }
        if (Run != null) {
            parts.Add("run " + Run.Command);
        }
        return string.Join(" and ", parts);
    }

    private static int ReadTimeout(IConfigurationSection section, string label, int defaultSeconds) {
        string? text = section["TimeoutSeconds"];
        if (string.IsNullOrWhiteSpace(text)) {
            return defaultSeconds;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0) {
            throw new PipelineConfigException($"{label}:TimeoutSeconds must be a positive whole number.");
        }
        return seconds;
    }
}

/// <summary>Carries out the OnSuccess or OnError action once the run has finished and the summary is written.</summary>
public static class PipelineActionRunner {
    /// <summary>
    /// Runs the webhook and then the program (each if configured). A failure in one does not stop the
    /// other. Returns true only if everything configured succeeded.
    /// </summary>
    public static bool Execute(
        string label,
        PipelineActionOptions options,
        PipelineSummary summary,
        string summaryJson,
        string? summaryPath,
        Action<string> log) {

        bool ok = true;
        if (options.Webhook != null) {
            ok &= CallWebhook(label, options.Webhook, summaryJson, log);
        }
        if (options.Run != null) {
            ok &= RunProgram(label, options.Run, summary, summaryPath, log);
        }
        return ok;
    }

    private static bool CallWebhook(string label, WebhookAction webhook, string summaryJson, Action<string> log) {
        try {
            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(webhook.TimeoutSeconds) })
            using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(webhook.Method), webhook.Url)) {
                request.Content = new StringContent(summaryJson, Encoding.UTF8, "application/json");
                foreach (KeyValuePair<string, string> header in webhook.Headers) {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) {
                        request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                using (HttpResponseMessage response = client.Send(request)) {
                    if (!response.IsSuccessStatusCode) {
                        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (body.Length > 200) {
                            body = body.Substring(0, 200) + "...";
                        }
                        Console.Error.WriteLine($"{label} webhook failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
                        return false;
                    }
                    log($"{label} webhook: HTTP {(int)response.StatusCode}");
                    return true;
                }
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"{label} webhook failed: {ex.Message}");
            return false;
        }
    }

    private static bool RunProgram(string label, RunAction run, PipelineSummary summary, string? summaryPath, Action<string> log) {
        string summaryText = summaryPath ?? "";
        try {
            ProcessStartInfo startInfo = new ProcessStartInfo {
                FileName = run.Command,
                UseShellExecute = false
            };
            if (run.WorkingDirectory != null) {
                startInfo.WorkingDirectory = run.WorkingDirectory;
            }
            foreach (string argument in run.Arguments) {
                startInfo.ArgumentList.Add(argument
                    .Replace("${summary}", summaryText, StringComparison.Ordinal)
                    .Replace("${status}", summary.Status, StringComparison.Ordinal)
                    .Replace("${pipeline}", summary.Pipeline, StringComparison.Ordinal));
            }
            startInfo.Environment["DATAPUDDLE_SUMMARY"] = summaryText;
            startInfo.Environment["DATAPUDDLE_STATUS"] = summary.Status;
            startInfo.Environment["DATAPUDDLE_PIPELINE"] = summary.Pipeline;

            log($"{label} run: {run.Command}");
            using (Process? process = Process.Start(startInfo)) {
                if (process == null) {
                    Console.Error.WriteLine($"{label} run failed: could not start '{run.Command}'.");
                    return false;
                }
                if (!process.WaitForExit(run.TimeoutSeconds * 1000)) {
                    process.Kill(true);
                    Console.Error.WriteLine($"{label} run failed: '{run.Command}' did not finish within {run.TimeoutSeconds} seconds and was stopped.");
                    return false;
                }
                if (process.ExitCode != 0) {
                    Console.Error.WriteLine($"{label} run failed: '{run.Command}' exited with code {process.ExitCode}.");
                    return false;
                }
                return true;
            }
        } catch (Win32Exception ex) {
            Console.Error.WriteLine($"{label} run failed: could not start '{run.Command}': {ex.Message}");
            return false;
        } catch (Exception ex) {
            Console.Error.WriteLine($"{label} run failed: {ex.Message}");
            return false;
        }
    }
}
