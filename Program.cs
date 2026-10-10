using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace DataPuddle;

internal static class Program {
    // The environment name picks which appsettings.<name>.json is layered over appsettings.json.
    // It comes from the DOTNET_ENVIRONMENT environment variable; without it, Debug builds use
    // "Development" and Release builds use "Production".
#if DEBUG
    private const string DefaultEnvironment = "Development";
#else
    private const string DefaultEnvironment = "Production";
#endif

    // Usage: DataPuddle [-i] [--reuse] [-f <configFile>] [-p name=value]... [--no-pipeline] [--dry-run] [connectionString] [outputDir]
    //   -i  open an interactive SQL shell against the local copy when the import finishes.
    //   --reuse  open the existing database file as it is (no recreate, no startup clones). The
    //       SQL Server connection is only needed if you later use .clone in the shell. Fails if the
    //       file does not exist. Use it to start from a copy of a previously cloned puddle.db.
    //   -p  set a pipeline parameter (repeatable): -p CustomerId=1234. Overrides the pipeline file's defaults.
    //   --no-pipeline  do not run the pipeline named in the config (handy with -i).
    //   --dry-run  load and validate the pipeline, print the plan, and exit without cloning or running anything.
    //   -f  (or --config) read settings from this JSON file instead of appsettings.json next to the
    //       executable. A relative path is resolved against the current directory. The environment
    //       file is looked up beside it: myproject.json is layered with myproject.<environment>.json.
    // Connection string precedence: first positional arg, then the SQLSERVER_CONNECTION
    // environment variable, then SQLSERVER_CONNECTION in appsettings.<environment>.json,
    // then SQLSERVER_CONNECTION in appsettings.json.
    // appsettings.json also holds "Tables" (schema.table names cloned at startup),
    // "TableFilters" (schema.table to an extra SQL Server condition, such as "IsInactive = 0") and
    // "WriteDelta" (true to also export each cloned table as Delta files; default false).
    private static int Main(string[] args) {
        bool interactive = false;
        string? configPath = null;
        bool reuse = false;
        bool noPipeline = false;
        bool dryRun = false;
        Dictionary<string, string> cliParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string> positionalList = new List<string>();
        for (int i = 0; i < args.Length; i++) {
            if (args[i] == "-i") {
                interactive = true;
            } else if (args[i] == "--reuse") {
                reuse = true;
            } else if (args[i] == "--no-pipeline") {
                noPipeline = true;
            } else if (args[i] == "--dry-run") {
                dryRun = true;
            } else if (args[i] == "-p") {
                int equals = i + 1 < args.Length ? args[i + 1].IndexOf('=') : -1;
                if (equals <= 0) {
                    Console.Error.WriteLine("-p requires name=value, e.g. -p CustomerId=1234");
                    return 2;
                }
                cliParameters[args[i + 1].Substring(0, equals)] = args[i + 1].Substring(equals + 1);
                i++;
            } else if (args[i] == "-f" || args[i] == "--config") {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) {
                    Console.Error.WriteLine($"{args[i]} requires a config file path, e.g. -f projects/acme.json");
                    return 2;
                }
                configPath = args[i + 1];
                i++;
            } else if (args[i].StartsWith('-')) {
                Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                return 2;
            } else {
                positionalList.Add(args[i]);
            }
        }
        string[] positional = positionalList.ToArray();

        string environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? DefaultEnvironment;

        // Default: appsettings.json beside the executable (optional, as before).
        // With -f: the named file, which must exist, with its environment file beside it.
        string configDirectory = AppContext.BaseDirectory;
        string configFileName = "appsettings.json";
        bool configRequired = false;
        if (configPath != null) {
            string fullConfigPath = Path.GetFullPath(configPath);
            if (!File.Exists(fullConfigPath)) {
                Console.Error.WriteLine($"Config file not found: {fullConfigPath}");
                return 2;
            }
            configDirectory = Path.GetDirectoryName(fullConfigPath) ?? Directory.GetCurrentDirectory();
            configFileName = Path.GetFileName(fullConfigPath);
            configRequired = true;
        }
        string environmentFileName =
            Path.GetFileNameWithoutExtension(configFileName) + "." + environmentName + Path.GetExtension(configFileName);

        IConfiguration config;
        try {
            config = new ConfigurationBuilder()
                .SetBasePath(configDirectory)
                .AddJsonFile(configFileName, optional: !configRequired)
                .AddJsonFile(environmentFileName, optional: true)
                .AddEnvironmentVariables()
                .Build();
        } catch (InvalidDataException ex) {
            Console.Error.WriteLine($"Could not read the config file: {ex.Message}");
            return 2;
        }
        Console.WriteLine($"Environment: {environmentName}");
        if (configPath != null) {
            Console.WriteLine($"Config: {Path.Combine(configDirectory, configFileName)}");
        }

        string outDir = positional.Length > 1 ? positional[1] : Path.Combine(Directory.GetCurrentDirectory(), "output");

        // Pipeline: the steps to run after the cloning, plus what to do when the run succeeds or fails.
        IConfigurationSection pipelineSection = config.GetSection("Pipeline");
        string? pipelineSetting = pipelineSection["File"];
        PipelineDefinition? pipeline = null;
        PipelineActionOptions? onSuccess = null;
        PipelineActionOptions? onError = null;
        if (!noPipeline && !string.IsNullOrWhiteSpace(pipelineSetting)) {
            try {
                string pipelinePath = Path.IsPathRooted(pipelineSetting)
                    ? pipelineSetting
                    : Path.Combine(configDirectory, pipelineSetting);
                Dictionary<string, string> parameterOverrides = new Dictionary<string, string>(cliParameters, StringComparer.OrdinalIgnoreCase);
                pipeline = PipelineDefinition.Load(pipelinePath, parameterOverrides);
                onSuccess = PipelineActionOptions.Load(pipelineSection.GetSection("OnSuccess"), "Pipeline:OnSuccess");
                onError = PipelineActionOptions.Load(pipelineSection.GetSection("OnError"), "Pipeline:OnError");
            } catch (PipelineConfigException ex) {
                Console.Error.WriteLine("Pipeline configuration error: " + ex.Message);
                return 2;
            }
        }

        if (dryRun) {
            if (pipeline == null) {
                Console.Error.WriteLine("--dry-run needs a pipeline: set Pipeline:File in the config" +
                    (noPipeline ? " and drop --no-pipeline." : "."));
                return 2;
            }
            PrintPlan(pipeline, onSuccess, onError);
            return 0;
        }

        string? connStr = positional.Length > 0 && !string.IsNullOrWhiteSpace(positional[0])
            ? positional[0]
            : config["SQLSERVER_CONNECTION"];
        if (string.IsNullOrWhiteSpace(connStr) && !reuse) {
            Console.Error.WriteLine($"No connection string found. Set SQLSERVER_CONNECTION in {configFileName} or the environment, or pass it as the first argument.");
            return 2;
        }

        List<string> tables = new List<string>();
        foreach (IConfigurationSection child in config.GetSection("Tables").GetChildren()) {
            if (!string.IsNullOrWhiteSpace(child.Value)) {
                tables.Add(child.Value);
            }
        }
        if (tables.Count > 0) {
            try {
                LocalStore.ParseTableList(tables);
            } catch (FormatException ex) {
                Console.Error.WriteLine($"Invalid entry in \"Tables\" in {configFileName}: " + ex.Message);
                return 2;
            }
        }

        Dictionary<string, string> filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection child in config.GetSection("TableFilters").GetChildren()) {
            if (!string.IsNullOrWhiteSpace(child.Value)) {
                filters[child.Key] = child.Value;
            }
        }

        bool writeDelta = false;
        string? writeDeltaText = config["WriteDelta"];
        if (!string.IsNullOrWhiteSpace(writeDeltaText) && !bool.TryParse(writeDeltaText, out writeDelta)) {
            Console.Error.WriteLine($"\"WriteDelta\" must be true or false, but was '{writeDeltaText}'.");
            return 2;
        }

        CopyOptions options = new CopyOptions {
            SqlConnectionString = connStr,
            OutputDirectory = outDir,
            Tables = tables,
            TableFilters = filters,
            WriteDelta = writeDelta
        };

        if (reuse && !File.Exists(options.DatabasePath)) {
            Console.Error.WriteLine($"--reuse needs an existing database, but none was found at {options.DatabasePath}. " +
                "Run once without --reuse to create it, or copy a puddle.db there.");
            return 2;
        }

        bool allMatch = true;
        bool startupFailed = false;
        string? startupError = null;

        PipelineSummary? summary = null;
        if (pipeline != null) {
            summary = new PipelineSummary {
                Pipeline = pipeline.Name,
                StartedUtc = DateTime.UtcNow,
                Environment = environmentName,
                ConfigFile = Path.Combine(configDirectory, configFileName),
                PipelineFile = pipeline.FilePath,
                Database = options.DatabasePath,
                CustomerId = ParseCustomerParameter(pipeline),
                Parameters = new Dictionary<string, string>(pipeline.Parameters)
            };
        }
        Stopwatch runWatch = Stopwatch.StartNew();
        bool pipelineSucceeded = true;

        using (LocalStore copier = new LocalStore(options)) {
            copier.Log = Console.WriteLine;

            try {
                if (reuse) {
                    copier.Open(recreate: false);
                    Console.WriteLine($"Reusing existing database: {options.DatabasePath}");
                    if (tables.Count > 0) {
                        Console.WriteLine("Startup tables are not cloned when --reuse is used.");
                    }
                    if (!interactive && pipeline == null) {
                        Console.WriteLine("Nothing to do: --reuse without -i or a pipeline only opens the database. Add -i for the shell.");
                    }
                } else {
                    copier.Open();

                    if (tables.Count == 0 && pipeline == null) {
                        Console.WriteLine($"No startup tables configured. Add a \"Tables\" list to {configFileName}" +
                            (interactive ? ", or use '.clone schema.table' below." : " or run with -i and use '.clone'."));
                    }

                    Stopwatch totalWatch = Stopwatch.StartNew();
                    IReadOnlyList<TableCopyResult> results = copier.CopyAll();
                    totalWatch.Stop();

                    foreach (TableCopyResult result in results) {
                        allMatch &= result.CountsMatch;
                        if (summary != null) {
                            summary.StartupClones.Add(new ResultSummary {
                                Table = result.FullName,
                                SqlCount = result.SqlCount,
                                LocalCount = result.LocalCount,
                                OutputCount = result.DeltaCount,
                                CountsMatch = result.CountsMatch
                            });
                        }
                    }

                    if (results.Count > 0) {
                        Console.WriteLine();
                        Console.WriteLine(writeDelta
                            ? "Timings (read, load, verification and Delta export):"
                            : "Timings (read, load and verification):");
                        foreach (TableCopyResult result in results) {
                            Console.WriteLine($"  {result.FullName,-20} {LocalStore.FormatElapsed(result.Elapsed)}");
                        }
                        Console.WriteLine($"  {"total",-20} {LocalStore.FormatElapsed(totalWatch.Elapsed)}");
                    }

                    if (!allMatch && summary != null) {
                        startupFailed = true;
                        startupError = "row counts do not match for: " + string.Join(", ",
                            results.Where(r => !r.CountsMatch).Select(r => r.FullName));
                    }
                }
            } catch (Exception ex) when (pipeline != null) {
                // With a pipeline, a startup failure is reported through the summary and the OnError action
                // instead of ending the program with an unhandled exception.
                startupFailed = true;
                startupError = ex.Message;
                allMatch = false;
                Console.Error.WriteLine("Startup failed: " + ex.Message);
            }

            if (pipeline != null && summary != null) {
                if (startupFailed) {
                    Console.Error.WriteLine("The pipeline was not run because the startup clones failed.");
                    summary.Error = new PipelineError { Step = "startup clones", Message = startupError ?? "unknown error" };
                    PipelineRunner.AddSkipped(pipeline, summary, 0);
                    pipelineSucceeded = false;
                } else {
                    PipelineRunner runner = new PipelineRunner(copier, Console.WriteLine);
                    pipelineSucceeded = runner.Run(pipeline, summary);
                }
                runWatch.Stop();
                summary.Status = pipelineSucceeded ? "success" : "failed";
                summary.FinishedUtc = DateTime.UtcNow;
                summary.DurationSeconds = Math.Round(runWatch.Elapsed.TotalSeconds, 3);
            }

            if (interactive && !startupFailed) {
                PuddleShell.Run(copier);
            }
        }

        // The database file is closed now, so a program started by an action can open it.
        int exitCode = allMatch && pipelineSucceeded ? 0 : 1;
        if (pipeline != null && summary != null) {
            string summaryJson = summary.ToJson();
            string? summaryPath = null;
            string? summarySetting = pipelineSection["Summary"];
            string summaryTarget = string.IsNullOrWhiteSpace(summarySetting)
                ? Path.Combine(outDir, "summary.json")
                : Path.GetFullPath(summarySetting);
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
            PipelineActionOptions? action = pipelineSucceeded && allMatch ? onSuccess : onError;
            string actionLabel = pipelineSucceeded && allMatch ? "OnSuccess" : "OnError";
            if (action != null) {
                bool actionOk = PipelineActionRunner.Execute(actionLabel, action, summary, summaryJson, summaryPath, Console.WriteLine);
                if (!actionOk && exitCode == 0) {
                    exitCode = 3;
                }
            }
        }

        Console.WriteLine($"Output: {outDir}");
        return exitCode;
    }

    // The summary's customerId comes from the CustomerId pipeline parameter when it is a whole number.
    private static long? ParseCustomerParameter(PipelineDefinition pipeline) {
        if (pipeline.Parameters.TryGetValue("CustomerId", out string? text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) {
            return value;
        }
        return null;
    }

    private static void PrintPlan(PipelineDefinition pipeline, PipelineActionOptions? onSuccess, PipelineActionOptions? onError) {
        Console.WriteLine($"Pipeline: {pipeline.Name} ({pipeline.FilePath})");
        if (pipeline.Parameters.Count > 0) {
            Console.WriteLine("Parameters:");
            foreach (KeyValuePair<string, string> parameter in pipeline.Parameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) {
                Console.WriteLine($"  {parameter.Key} = {parameter.Value}");
            }
        }
        Console.WriteLine("Steps:");
        foreach (PipelineStep step in pipeline.Steps) {
            Console.WriteLine($"  {step.Index,2}. {step.Name} ({step.Describe()})");
        }
        Console.WriteLine("On success: " + (onSuccess != null ? onSuccess.Describe() : "nothing configured"));
        Console.WriteLine("On error:   " + (onError != null ? onError.Describe() : "nothing configured"));
        Console.WriteLine("Dry run only: nothing was cloned or run.");
    }
}
