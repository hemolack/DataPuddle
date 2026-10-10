# DataPuddle

DataPuddle clones tables from SQL Server into a small local database file, so you can run lookups, transforms and validation with plain SQL on your own machine instead of against production. When the work is done you can export any local table as Delta, CSV, TSV or any single-character-delimited text file.

Typical flow:

1. Clone the tables you need (`dbo.invoices`, `dbo.lineitems`, ...) from SQL Server.
2. Import delimited files from a client, then query, join, clean and reshape everything locally.
3. Export the result in a form the main database can ingest with no further transformation.

- [Getting started](#getting-started)
- [Configuration](#configuration)
- [Command line](#command-line)
- [Pipelines: non-interactive runs](#pipelines-non-interactive-runs)
- [The interactive shell](#the-interactive-shell)
- [Importing delimited files](#importing-delimited-files)
- [Exporting](#exporting)
- [SQL Server vs. the local SQL dialect](#sql-server-vs-the-local-sql-dialect)
- [Column type mapping](#column-type-mapping)
- [Using DataPuddle from your own code](#using-datapuddle-from-your-own-code)
- [Notes and limitations](#notes-and-limitations)

## Getting started

Requires the .NET 8 SDK.

```bash
dotnet run -- -i
```

On startup DataPuddle reads its configuration, connects to SQL Server, clones every table listed under `Tables`, prints row counts and timings, and (with `-i`) drops you into the interactive shell. The local database is written to `output/puddle.db`.

## Configuration

Settings come from, in increasing priority:

1. `appsettings.json` beside the executable (committed, placeholder values), or the file named with `-f`
2. `appsettings.<environment>.json` (git-ignored, your real values), or `<name>.<environment>.json` beside the `-f` file
3. Environment variables
4. Command-line arguments (connection string and output folder only)

The environment name comes from `DOTNET_ENVIRONMENT`. If it is not set, Debug builds use `Development` and Release builds use `Production`. The name in use is printed at startup.

```json
{
  "SQLSERVER_CONNECTION": "Server=localhost;Database=YourDatabase;Integrated Security=true;TrustServerCertificate=true",
  "WriteDelta": false,
  "Tables": [ "dbo.invoices", "dbo.lineitems" ],
  "TableFilters": { "dbo.invoices": "IsInactive = 0" }
}
```

| Setting | Meaning |
|---|---|
| `SQLSERVER_CONNECTION` | SQL Server connection string. A first positional argument overrides it. |
| `Tables` | `schema.table` names cloned at startup. May be empty, in which case you clone on demand from the shell. |
| `TableFilters` | Extra SQL Server condition per table, keyed by `schema.table` (or just the table name). It is applied to both the load and the verification count, so counts still agree. |
| `WriteDelta` | When `true`, every cloned table is also written as a Delta table. Default `false`. |

Put your real server and database in `appsettings.Development.json`; it is excluded by `.gitignore`.

### One executable, many projects

Use `-f` (or `--config`) to point at a different config file, so each project keeps its own settings and you only need one build of DataPuddle:

```bash
DataPuddle -i -f projects/acme/puddle.json
```

- Without `-f`, the `appsettings.json` next to the executable is used, exactly as before.
- A relative path is resolved against the current directory. The file must exist (a missing `-f` file is an error rather than being silently ignored), and the path in use is printed at startup as `Config: ...`.
- The environment file is looked up beside the file you name, with the environment inserted before the extension: `puddle.json` is layered with `puddle.Development.json`. Environment variables still override both.
- Relative output folders are not tied to the config file: the default `./output` is relative to the folder you run the command from. Run from the project folder, or pass `outputDir` explicitly, to keep each project's database separate.
- The `appsettings.*.json` ignore rule in `.gitignore` only covers files with that name. If you keep project configs holding real connection strings in a repository, name them to match (for example `acme.local.json` is already ignored) or add your own ignore rule.

Note: JSON arrays merge by index across config layers, so a `Tables` list in `appsettings.Development.json` overrides entries of the base list position by position rather than replacing the whole list.

## Command line

```
DataPuddle [-i] [--reuse] [-f <configFile>] [-p name=value]... [--no-pipeline] [--dry-run] [connectionString] [outputDir]
```

| Option | Meaning |
|---|---|
| `-i` | Open the interactive shell after the startup clones finish. |
| `--reuse` | Open the existing database file as it is, instead of recreating it, and skip the startup clones. See [Starting from a copy](#starting-from-a-copy). |
| `-f <path>`, `--config <path>` | Read settings from this JSON file instead of `appsettings.json`. See [One executable, many projects](#one-executable-many-projects). |
| `-p name=value` | Set a pipeline parameter (repeatable). Overrides the pipeline file's defaults. See [Pipelines](#pipelines-non-interactive-runs). |
| `--no-pipeline` | Skip the pipeline named in the config (useful with `-i` when you want to explore first). |
| `--dry-run` | Load and validate the pipeline, print the plan, and exit without cloning or running anything. |
| `connectionString` | Overrides `SQLSERVER_CONNECTION`. |
| `outputDir` | Where the database file and exports go. Default `./output`. |

Exit codes: `0` success; `1` a clone had mismatched row counts or a pipeline step failed; `2` a configuration, argument or pipeline-file error (reported before anything runs); `3` the run succeeded but the `OnSuccess` action failed.

Each run starts with a fresh database file: the previous `puddle.db` is replaced when the program starts (unless you use `--reuse`). Cloning a table that already exists in the session replaces it.

Only one process can open the file for writing at a time. Several processes can open it read-only.

### Starting from a copy

To run several independent processes from the same snapshot, clone once and then copy the database file:

```bash
DataPuddle                       # clones the configured tables into ./output/puddle.db
cp output/puddle.db ../job1/output/puddle.db
cp output/puddle.db ../job2/output/puddle.db

cd ../job1 && DataPuddle --reuse -i
```

With `--reuse`:

- the existing `puddle.db` is opened as it is, so every cloned table and anything you created is already there;
- the startup `Tables` are not cloned, and SQL Server is not contacted;
- no connection string is needed unless you later use `.clone` in the shell (then the usual connection string rules apply);
- it is an error if the file does not exist (`output/puddle.db` under the current folder, or under `outputDir` if you pass one);
- without `-i` it just opens the database and exits, so combine it with `-i`.

Copy the file while no DataPuddle process has it open, so the copy is complete. Only one process can have a given file open for writing, which is why each job gets its own copy. If you pass `outputDir` while also relying on the configured connection string, give an empty first argument: `DataPuddle --reuse -i "" ../job1/output`.

## Pipelines: non-interactive runs

A pipeline is an ordered list of steps that runs automatically after the startup clones finish (or straight after opening the database with `--reuse`). Each step is SQL, a clone, an export or an assertion. The first step that fails stops the run, an `OnError` action fires, and a JSON summary of the run is written either way. When everything succeeds, an `OnSuccess` action fires.

Two pieces are involved: the **pipeline file** (the steps) and the **`Pipeline` section of the config file** (where the file is, where the summary goes, and what to do on success or failure).

A complete working example is in the `samples` folder: `sample_pipeline.json` and the `clean.sql` file it uses. Check it with `DataPuddle --dry-run` after pointing `Pipeline:File` at it.

### Config

```json
{
  "Tables": [ "dbo.invoices", "dbo.lineitems" ],
  "Pipeline": {
    "File": "samples/sample_pipeline.json",
    "Summary": "runs/acme/summary.json",
    "OnSuccess": {
      "Webhook": {
        "Url": "https://hooks.example.com/puddle/success",
        "Headers": { "Authorization": "Bearer <token>" }
      }
    },
    "OnError": {
      "Webhook": { "Url": "https://hooks.example.com/puddle/failed" },
      "Run": { "Command": "notify-oncall", "Arguments": [ "--summary", "${summary}" ] }
    }
  }
}
```

| Setting | Meaning |
|---|---|
| `Pipeline:File` | The pipeline file. A relative path is resolved against the folder of the config file, so each project's config can point at its own pipeline. |
| `Pipeline:Summary` | Where to write the summary JSON. Default `summary.json` in the output folder. A relative path here is relative to the current directory. |
| `Pipeline:OnSuccess`, `Pipeline:OnError` | What to do when the run ends. Each can have a `Webhook`, a `Run`, or both (the webhook goes first, and a failure in one does not stop the other). |

Put secrets such as tokens in `appsettings.<environment>.json` (git-ignored) or in environment variables, for example `Pipeline__OnSuccess__Webhook__Headers__Authorization`.

**Webhook** options: `Url` (http or https, required), `Method` (`POST` default, `PUT` or `PATCH`), `Headers`, `TimeoutSeconds` (default 30). The request body is the summary JSON (`Content-Type: application/json`). Any non-2xx response counts as a failure.

**Run** options: `Command` (required), `Arguments` (a list, passed as separate arguments without a shell), `WorkingDirectory`, `TimeoutSeconds` (default 300; the process is stopped if it runs longer). Within `Arguments`, `${summary}` (path of the summary file), `${status}` (`success` or `failed`) and `${pipeline}` (the pipeline name) are replaced. The same values are available to the program as the environment variables `DATAPUDDLE_SUMMARY`, `DATAPUDDLE_STATUS` and `DATAPUDDLE_PIPELINE`. A non-zero exit code counts as a failure.

The actions run after the database file has been closed, so a program you start can open `puddle.db` itself. If you use `-i`, the shell opens after the pipeline finishes and the actions run when you leave it.

### Pipeline file

```json
{
  "name": "acme-import",
  "parameters": { "CustomerId": "1234" },
  "steps": [
    {
      "name": "load feed",
      "sql": "CREATE TABLE stage.invoice_in AS SELECT * FROM read_csv('feeds/${CustomerId}/invoices.csv', delim = '|', header = true, all_varchar = true)"
    },
    { "name": "clean", "file": "clean.sql" },
    { "name": "reference tables", "clone": [ "dbo.client", "dbo.provider" ] },
    {
      "name": "no duplicate invoices",
      "assert": "SELECT InvoiceId FROM stage.invoice_clean GROUP BY InvoiceId HAVING count(*) > 1"
    },
    { "name": "write output", "export": "delimited", "delimiter": "|", "tables": [ "stage.invoice_clean" ] }
  ]
}
```

The file may contain `//` and `/* */` comments and trailing commas. Every step has an optional `name` (default `step N`), an optional `continueOnError` (see below) and exactly one of these:

| Step | Keys | What it does |
|---|---|---|
| SQL | `sql` | One string, or a list of strings, holding one or more statements. |
| SQL file | `file` | A `.sql` file with one or more statements. A relative path is resolved against the pipeline file's folder. |
| Clone | `clone` | A table list (`"dbo.a, dbo.b"` or `["dbo.a", "dbo.b"]`): the same as `.clone`, including `TableFilters`. Fails if any row count does not match. |
| Export | `export` (`delta`, `csv`, `tsv` or `delimited`), `tables`, and `delimiter` for `delimited` | The same as `.export`. Fails if an exported row count does not match. |
| Assert | `assert`, optional `expectRows` | Runs one query and fails unless it returns exactly `expectRows` rows (default `0`, so the query lists the problems). The failure message includes the first few rows returned. |

### Continuing after a failed step

By default the first failing step ends the run. Add `"continueOnError": true` to a step that is allowed to fail:

```json
{ "name": "optional lookup refresh", "clone": [ "dbo.provider_extra" ], "continueOnError": true }
```

If that step fails, the failure is logged and recorded and the run carries on with the next step. It works on every step type. Details:

- Only steps that set it continue; any other failing step still stops the run, fires `OnError` and skips the rest.
- Whatever the failed step already did stays in place (each statement commits on its own), so a later step may see a partly changed database.
- A run whose only failures were ignored this way ends with status `success`, so `OnSuccess` fires and the exit code is `0`. The summary shows what was ignored: the step has `"status": "failed"` with `"continuedAfterFailure": true`, and the top level has `"ignoredFailures": <count>`. Check that field if your `OnSuccess` handler needs to tell a clean run from one with ignored failures.
- An assertion with `continueOnError` is a warning: it is reported in the summary but does not stop anything.

Notes on SQL steps:

- Statements are split at semicolons; semicolons inside strings, quoted identifiers and comments are fine. Dollar-quoted strings (`$$ ... $$`) are not recognized by the splitter.
- `SELECT ... INTO` is translated, as in the shell. Dot-commands (`.export`, ...) are not used in pipelines; use the clone and export steps.
- Each statement commits on its own. A failed step does not undo the statements that already ran, and later steps are not run. The failure message names the statement and shows its first part.

Everything in the file is checked before anything is cloned or run: unknown keys, missing files, bad table names, a `delimited` export without a delimiter, and unresolved parameters are all reported together with the step they belong to, and the program exits with code `2`. `--dry-run` stops after that check and prints the plan.

### Parameters

`${Name}` in any `sql`, `file` (both the path and the contents), `clone`, `export`, `assert` or `delimiter` text is replaced before the pipeline runs. Values come from the `parameters` object in the pipeline file (defaults), then `-p Name=value` on the command line, which wins. A parameter with no value is an error. Names are case-insensitive and use letters, digits and underscores.

The replacement is plain text, so write the quotes yourself where the value is a string (`WHERE Region = '${Region}'`). Parameter values are written to the summary file, so do not pass secrets this way.

### One run per client

The pipeline is designed for one run per client. Which rows are cloned is decided by `TableFilters` in the config file, and `${CustomerId}` is a pipeline parameter, so a run for one client looks like this:

```bash
DataPuddle -f clients/1234.json -p CustomerId=1234 "" runs/1234
```

where `clients/1234.json` holds that client's settings:

```json
{
  "Tables": [ "dbo.invoices", "dbo.lineitems" ],
  "TableFilters": {
    "dbo.invoices": "CustomerID = 1234",
    "dbo.lineitems": "CustomerID = 1234"
  },
  "Pipeline": { "File": "../samples/sample_pipeline.json" }
}
```

The output folder (the second positional argument) holds that client's database, exports and summary. To process several clients, start the program once per client with its own config file and output folder; the empty first argument keeps the connection string from the config (put it in a shared `appsettings.json` or an environment variable):

```bash
for id in 1234 1487 1502; do
  DataPuddle -f "clients/$id.json" -p CustomerId="$id" "" "runs/$id" || echo "client $id failed"
done
```

Leave `Pipeline:Summary` unset in that case, so each run writes `summary.json` into its own output folder.

### The summary file

Written after every run, whether it succeeded or not (unless the pipeline file itself was invalid). Property names are camelCase and unset values are left out.

```json
{
  "pipeline": "acme-import",
  "status": "failed",
  "startedUtc": "2026-10-09T19:41:02.113Z",
  "finishedUtc": "2026-10-09T19:41:58.870Z",
  "durationSeconds": 56.757,
  "environment": "Production",
  "configFile": "/data/acme/puddle.json",
  "pipelineFile": "/data/acme/samples/sample_pipeline.json",
  "database": "/data/acme/runs/1234/puddle.db",
  "customerId": 1234,
  "parameters": { "CustomerId": "1234" },
  "startupClones": [
    { "table": "dbo.invoices", "sqlCount": 212946, "localCount": 212946, "countsMatch": true }
  ],
  "steps": [
    { "index": 1, "name": "load feed", "kind": "sql", "status": "success", "durationSeconds": 1.2, "detail": "1 statement(s)" },
    { "index": 2, "name": "clean", "kind": "sql", "status": "success", "durationSeconds": 3.9, "detail": "4 statement(s)" },
    { "index": 4, "name": "no duplicate invoices", "kind": "assert", "status": "failed", "durationSeconds": 0.1,
      "error": "assertion returned 12 row(s), expected 0. First rows: InvoiceId=1007 | InvoiceId=1033" },
    { "index": 5, "name": "write output", "kind": "export", "status": "skipped", "durationSeconds": 0 }
  ],
  "error": { "step": "no duplicate invoices", "message": "assertion returned 12 row(s), expected 0. First rows: InvoiceId=1007 | InvoiceId=1033" }
}
```

Step `status` is `success`, `failed` or `skipped`. Clone and export steps also carry a `results` list with the row counts per table (and the file location for exports). If the startup clones fail (an error, or row counts that do not match), the steps are all `skipped` and `error.step` is `startup clones`.

### Combining with other options

- `--reuse` skips the startup clones and runs the pipeline against the existing database, so a pipeline whose first step is a `clone` can add tables to a seeded copy.
- `-i` opens the shell after the pipeline so you can inspect the result; `--no-pipeline -i` opens the shell without running the pipeline.
- `--dry-run` never opens the database, contacts SQL Server or fires an action.

## The interactive shell

Start it with `-i`. Type SQL ending with `;` (statements can span lines). Commands start with a period and must be on one line:

| Command | Description |
|---|---|
| `.clone <schema.table>[, ...]` | Clone more tables from SQL Server, e.g. `.clone dbo.invoices, dbo.lineitems`. Does the same thing as the startup clone, including the filters from `TableFilters`. |
| `.export <format> <schema.table>[, ...]` | Export local tables. See [Exporting](#exporting). |
| `.tables` | List tables. |
| `.schema <table>` | Show a table's columns and types, e.g. `.schema dbo.invoices`. |
| `.help` | Show the command list. |
| `.quit` / `.exit` / `.q` | Leave the shell. |

Query results show at most 1,000 rows, with columns truncated at 60 characters. The data lives in the file, so changes you make (new tables, updates) are kept for the rest of the session and exported on request.

### SELECT ... INTO

Because the local dialect has no `SELECT ... INTO`, the shell rewrites it for you before running it, and prints what it ran:

```
puddle> select * into stage.invoices from dbo.invoices where CustomerID = 1234;
-- translated to: CREATE TABLE "stage"."invoices" AS select * from dbo.invoices where CustomerID = 1234;
```

The target can be `name`, `schema.name` or `catalog.schema.name`, written bare, in `"double quotes"` or in `[brackets]`. A missing schema is created automatically. Works with CTEs (`WITH ... SELECT ... INTO`), joins, `UNION`, sub-queries and so on. As in SQL Server, it is an error if the target table already exists. Not translated: `#temp` targets, `@variable` targets and `SELECT TOP n ... INTO` (use `LIMIT`, and `CREATE TEMP TABLE` for temporary tables). Source tables written with `[brackets]` are not rewritten; see below.

## Importing delimited files

Delimited files are read directly with SQL, no separate import step. Paths are relative to the folder you launched DataPuddle from.

```sql
-- Look at a file without importing it (format and column types are auto-detected)
SELECT * FROM 'invoices.csv';

-- Create a table from a file
CREATE TABLE stage.invoice_in AS SELECT * FROM read_csv('invoices.csv');

-- Load into a table that already exists
COPY stage.invoice_in FROM 'invoices.csv' (HEADER, DELIMITER ',');
```

Auto-detection samples the file to guess the delimiter, quoting, header row and column types. That is convenient for exploring, but for a real feed you should spell everything out, particularly **date formats**: a value like `01/02/2024` is ambiguous and will be guessed one way or the other.

```sql
CREATE TABLE stage.invoice_in AS
SELECT * FROM read_csv('feed.txt',
    delim      = '|',
    header     = true,
    columns    = {'InvoiceId': 'INTEGER', 'CustomerId': 'INTEGER', 'InvoiceDate': 'DATE'},
    dateformat = '%m/%d/%Y',
    nullstr    = '');
```

Common options for `read_csv` (and `COPY ... FROM`, which accepts the same ideas in upper case):

| Option | Purpose |
|---|---|
| `delim` | Field delimiter: `','`, `'\|'`, `';'`, `'\t'`, ... |
| `header` | `true` if the first line holds column names. |
| `skip` | Number of lines to skip before the data. |
| `columns` | Explicit column names and types. Turns off type guessing. |
| `all_varchar` | Read every column as text; convert later with `CAST`. A good first step for messy files. |
| `quote`, `escape` | Quote and escape characters. |
| `nullstr` | Text that means NULL (for example `''` or `'NULL'`). |
| `dateformat`, `timestampformat` | `strptime`-style patterns such as `'%m/%d/%Y'` and `'%m/%d/%Y %H:%M:%S'`. |
| `ignore_errors` | Skip rows that fail to parse instead of stopping. |
| `store_rejects` | Keep bad rows for inspection (see below). |
| `null_padding` | Pad short rows with NULLs. |
| `union_by_name` | When reading several files, match columns by name instead of position. |
| `filename` | Add a column holding each row's source file. |

**Several files at once.** Globs and lists work:

```sql
SELECT * FROM read_csv('feeds/*.csv', union_by_name = true, filename = true);
```

**Finding bad rows.** With `store_rejects = true` (use it together with `ignore_errors = true`), rows that could not be parsed are saved with the reason:

```sql
CREATE TABLE stage.invoice_in AS
SELECT * FROM read_csv('feed.txt', delim = '|', header = true,
    columns = {'InvoiceId': 'INTEGER', 'CustomerId': 'INTEGER', 'InvoiceDate': 'DATE'},
    dateformat = '%m/%d/%Y', store_rejects = true, ignore_errors = true);

SELECT * FROM reject_errors;   -- line, column, offending text, and the error message
```

**Handy patterns**

```sql
-- Read everything as text first, then validate before converting
CREATE TABLE stage.raw AS SELECT * FROM read_csv('feed.txt', delim = '|', all_varchar = true);
SELECT * FROM stage.raw WHERE TRY_CAST(CustomerId AS INTEGER) IS NULL;   -- rows that will not convert

-- Compare an incoming file against a cloned table
SELECT f.InvoiceId FROM read_csv('feed.txt', delim = '|') f
LEFT JOIN dbo.invoices i ON i.InvoiceId = f.InvoiceId
WHERE i.InvoiceId IS NULL;
```

Gzip-compressed files (`.gz`) are read directly. Other readers are available in the same style, for example `read_parquet('x.parquet')` and `read_json('x.json')`.

## Exporting

```
.export delta <schema.table>[, ...]
.export csv <schema.table>[, ...]
.export tsv <schema.table>[, ...]
.export delimited <char> <schema.table>[, ...]
```

Any local table can be exported, whether it was cloned or created with your own SQL. For `delimited`, give one character: `|`, `;`, a quoted character such as `';'`, or `\t` / `tab`. The delimiter cannot be a double quote, a line break or a control character other than tab.

| Format | Output |
|---|---|
| `delta` | `output/delta/<schema>/<table>/` (Parquet data files plus a `_delta_log`) |
| `csv`, `tsv`, `delimited` | `output/export/<schema>.<table>.csv`, `.tsv` or `.txt` |

Delimited exports include a header row, quote values when needed, and write NULL as an empty field. After each export the row count of the file is compared with the table, and `OK` or `MISMATCH` is printed.

The Delta log is written by DataPuddle itself and has not been verified against every Delta reader; check it with the reader you plan to use before relying on it. Nested column types cannot be exported to Delta.

## SQL Server vs. the local SQL dialect

The local database speaks standard-leaning SQL with a number of conveniences, but it is not T-SQL. The differences that matter when moving queries over:

### Names, quoting and structure

- **Identifiers use "double quotes", not [brackets].** `[brackets]` build list values here, so `SELECT [Invoice Id] FROM ...` does not do what it does in SQL Server. Rename or quote with `"Invoice Id"`.
- **Strings use 'single quotes'** (as in SQL Server). Double quotes are for identifiers only.
- **The default schema is `main`, not `dbo`.** Cloned tables keep their SQL Server schema (`dbo.invoices` stays `dbo.invoices`). An unqualified `invoices` will not find it; write `dbo.invoices`. Tables you create without a schema land in `main`.
- **Temporary tables have no `#` prefix:** `CREATE TEMP TABLE work AS SELECT ...`.
- **No `GO` batch separator, no `DECLARE`/`@variables`, no `IF`/`WHILE` control flow, and no stored procedures.** Everything is a single SQL statement ending in `;`. Use CTEs, `CREATE MACRO` (reusable expressions and queries) and ordinary scripts of statements instead of procedural code.
- **No `IDENTITY` columns.** Use a sequence: `CREATE SEQUENCE s; ... DEFAULT nextval('s')`.
- **Cloned tables carry column names, types and `NOT NULL` only.** Primary keys, foreign keys, indexes, defaults and triggers are not copied.
- **Transactions** use `BEGIN; ... COMMIT;`.

### Behavior that differs from what you expect

- **String comparison is case-sensitive.** SQL Server's default collation is not, so `WHERE Name = 'smith'` will no longer match `Smith`. Use `lower(Name) = 'smith'` or `ILIKE`.
- **Trailing spaces matter.** `'a' = 'a  '` is false here; SQL Server treats them as equal. `trim()` when in doubt.
- **`+` does not concatenate strings.** Use `||` (or `concat(a, b)`). `'a' + 'b'` is an error.
- **`/` on integers returns a decimal.** `7 / 2` is `3.5`, not `3`. Use `//` for integer division: `7 // 2` is `3`.
- **NULLs sort last** in ascending order (SQL Server sorts them first). Use `ORDER BY x NULLS FIRST` to match.
- **`bit` columns become `BOOLEAN`.** `WHERE IsInactive = 0` still works, and `= false` is clearer.
- **`UPDATE` joins use `UPDATE t SET col = o.col FROM other o WHERE t.k = o.k`.** Don't repeat the target table in the `FROM` clause as T-SQL does. `MERGE INTO ... USING ... ON ...` is supported.

### Function and syntax equivalents

| SQL Server | Here |
|---|---|
| `SELECT TOP 10 ...` | `SELECT ... LIMIT 10` |
| `ISNULL(a, b)` | `COALESCE(a, b)` or `IFNULL(a, b)` |
| `IIF(c, a, b)` | `CASE WHEN c THEN a ELSE b END` or `if(c, a, b)` |
| `GETDATE()`, `SYSDATETIME()` | `now()` or `current_timestamp` |
| `LEN(s)` | `length(s)` (`len(s)` also works) |
| `CHARINDEX(find, s)` | `strpos(s, find)` (note the argument order) |
| `REPLICATE(s, n)` | `repeat(s, n)` |
| `DATEADD(day, 1, d)` | `d + INTERVAL 1 DAY` |
| `DATEDIFF(day, a, b)` | `datediff('day', a, b)` (the part is a quoted string) |
| `EOMONTH(d)` | `last_day(d)` |
| `CONVERT(int, x)` | `CAST(x AS INTEGER)` or `x::INTEGER` |
| `TRY_CONVERT(int, x)` | `TRY_CAST(x AS INTEGER)` |
| `STRING_AGG(x, ',')` | `string_agg(x, ',')` (same) |
| `FORMAT(n, 'N0')` | `format('{:,}', n)` |
| `SELECT ... INTO t FROM ...` | `CREATE TABLE t AS SELECT ... FROM ...` (the shell does this for you) |
| `SELECT * INTO #t FROM ...` | `CREATE TEMP TABLE t AS SELECT * FROM ...` |

### Things you gain

- `SELECT * EXCLUDE (col)` and `SELECT * REPLACE (expr AS col)`
- `GROUP BY ALL` and `ORDER BY ALL`
- `QUALIFY` to filter on window functions (`... QUALIFY row_number() OVER (...) = 1`)
- `x::TYPE` casts, list and struct values, and `FROM`-first queries (`FROM dbo.invoices WHERE ...`)
- Direct querying of files: `SELECT * FROM 'file.csv'`

## Column type mapping

| SQL Server | Local type | Delta type |
|---|---|---|
| `bit` | `BOOLEAN` | boolean |
| `tinyint`, `smallint` | `SMALLINT` | short |
| `int` | `INTEGER` | integer |
| `bigint` | `BIGINT` | long |
| `real`; `float` (precision ≤ 24) | `FLOAT` | float |
| `float` (larger) | `DOUBLE` | double |
| `decimal(p,s)`, `numeric(p,s)` | `DECIMAL(p,s)` | decimal(p,s) |
| `money`, `smallmoney` | `DECIMAL(19,4)`, `DECIMAL(10,4)` | decimal |
| `date` | `DATE` | date |
| `time` | `TIME` | string |
| `datetime`, `datetime2`, `smalldatetime` | `TIMESTAMP` | timestamp (values treated as UTC) |
| `datetimeoffset` | `TIMESTAMPTZ` | timestamp |
| `char`, `varchar`, `nchar`, `nvarchar`, `text`, `ntext`, `xml`, `sysname` | `VARCHAR` (length limits are dropped) | string |
| `uniqueidentifier` | `UUID` | string |
| `binary`, `varbinary`, `image`, `timestamp`/`rowversion` | `BLOB` | binary |

Any other SQL Server type stops the clone with a message naming the column.

When you export a table you created locally, types are mapped on the way out in the same spirit (for example unsigned 64-bit integers become `decimal(20,0)` and enumerations become strings).

## Using DataPuddle from your own code

Everything the program does is in the `LocalStore` class, so another application can use the same local database directly:

```csharp
CopyOptions options = new CopyOptions {
    SqlConnectionString = connectionString,
    OutputDirectory = "work",
    Tables = new[] { "dbo.invoices", "dbo.lineitems" },
    TableFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["dbo.invoices"] = "IsInactive = 0"
    }
};

using (LocalStore store = new LocalStore(options)) {
    store.Log = Console.WriteLine;
    store.Open();
    store.CopyAll();                        // the configured Tables
    store.Clone("dbo.client, dbo.provider"); // more tables on demand

    store.Execute("CREATE TABLE stage.clean AS SELECT ... FROM dbo.invoices");

    store.Export(ExportFormat.Csv, "stage", "clean", null);
}
```

`store.Connection` exposes the open connection for your own queries. The sources in this project are the reference: `LocalStore.cs` (clone, export, type mapping), `SelectIntoTranslator.cs`, and `PuddleShell.cs`.

## Notes and limitations

- Cloning reads each table in full (with its filter), so for very large tables run it against a replica or off-hours.
- Row counts are checked after every clone and export; a mismatch is reported and makes the exit code non-zero.
- `.clone` and `.export` take table lists on one line. Names may be written with optional `[brackets]`.
- Error messages from SQL you type in the shell come straight from the SQL engine and are worded accordingly.
