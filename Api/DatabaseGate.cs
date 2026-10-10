using DuckDB.NET.Data;
using Microsoft.AspNetCore.Http;

namespace DataPuddle.Api;

/// <summary>How a request uses the database.</summary>
public enum AccessMode {
    /// <summary>Inside a read-only transaction that is always rolled back.</summary>
    Read,

    /// <summary>Inside a transaction that is committed only when the caller says so.</summary>
    Write,

    /// <summary>No transaction: the caller has the database to itself (clone, export, pipeline runs, autocommit SQL).</summary>
    Exclusive
}

/// <summary>
/// Lets one request at a time use the database. A DuckDB file can only be opened by one process, and the
/// API shares the single connection the rest of the program uses, so requests take turns.
/// </summary>
public sealed class DatabaseGate : IDisposable {
    private readonly LocalStore _store;
    private readonly ApiOptions _options;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    public DatabaseGate(LocalStore store, ApiOptions options) {
        _store = store;
        _options = options;
    }

    public LocalStore Store {
        get {
            return _store;
        }
    }

    /// <summary>Waits for the database (up to Api:LockWaitSeconds) and starts the requested kind of access.</summary>
    public async Task<DatabaseSession> EnterAsync(AccessMode mode, CancellationToken cancellationToken, HttpContext? context = null) {
        bool acquired = await _lock.WaitAsync(TimeSpan.FromSeconds(_options.LockWaitSeconds), cancellationToken);
        if (!acquired) {
            throw ApiException.Busy();
        }

        try {
            DatabaseSession session = new DatabaseSession(_store.Connection, mode, _lock, _options.QueryTimeoutSeconds, cancellationToken, context);
            session.Begin();
            return session;
        } catch {
            _lock.Release();
            throw;
        }
    }

    public void Dispose() {
        _lock.Dispose();
    }
}

/// <summary>
/// One request's turn with the database. Disposing it ends the transaction (rolling back anything not
/// committed) and lets the next request in.
/// </summary>
public sealed class DatabaseSession : IDisposable {
    private readonly DuckDBConnection _connection;
    private readonly AccessMode _mode;
    private readonly SemaphoreSlim _lock;
    private readonly int _timeoutSeconds;
    private readonly CancellationToken _cancellationToken;
    private readonly HttpContext? _context;
    private bool _inTransaction;
    private bool _released;

    internal DatabaseSession(
        DuckDBConnection connection,
        AccessMode mode,
        SemaphoreSlim gateLock,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        HttpContext? context) {
        _connection = connection;
        _mode = mode;
        _lock = gateLock;
        _timeoutSeconds = timeoutSeconds;
        _cancellationToken = cancellationToken;
        _context = context;
    }

    public AccessMode Mode {
        get {
            return _mode;
        }
    }

    public DuckDBConnection Connection {
        get {
            return _connection;
        }
    }

    internal void Begin() {
        switch (_mode) {
            case AccessMode.Read:
                RunStatement("START TRANSACTION READ ONLY");
                _inTransaction = true;
                break;
            case AccessMode.Write:
                RunStatement("BEGIN TRANSACTION");
                _inTransaction = true;
                break;
        }
    }

    /// <summary>Makes a Write session's changes permanent. Does nothing for other modes.</summary>
    public void Commit() {
        if (_inTransaction && _mode == AccessMode.Write) {
            RunStatement("COMMIT");
            _inTransaction = false;
        }
    }

    /// <summary>Creates a command with the given SQL and optional positional (?) or named ($name) parameters.</summary>
    public DuckDBCommand CreateCommand(string sql, IEnumerable<object?>? positional = null, IReadOnlyDictionary<string, object?>? named = null) {
        DuckDBCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        if (positional != null) {
            foreach (object? value in positional) {
                command.Parameters.Add(new DuckDBParameter(value ?? DBNull.Value));
            }
        }
        if (named != null) {
            foreach (KeyValuePair<string, object?> pair in named) {
                command.Parameters.Add(new DuckDBParameter(pair.Key, pair.Value ?? DBNull.Value));
            }
        }
        return command;
    }

    /// <summary>Runs a statement that returns no rows.</summary>
    public void Execute(string sql) {
        using (DuckDBCommand command = CreateCommand(sql)) {
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Cancels the command if it runs past Api:QueryTimeoutSeconds, or if the caller disconnects.
    /// Dispose the returned timer when the command (and any reader on it) is finished.
    /// </summary>
    public QueryTimer StartTimer(DuckDBCommand command) {
        return new QueryTimer(command, _timeoutSeconds, _cancellationToken, _context);
    }

    private void RunStatement(string sql) {
        using (DuckDBCommand command = _connection.CreateCommand()) {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    public void Dispose() {
        if (_released) {
            return;
        }
        _released = true;
        try {
            if (_inTransaction) {
                RunStatement("ROLLBACK");
                _inTransaction = false;
            }
        } catch (Exception) {
            // The connection may already have ended the transaction after an error; nothing more to undo.
        } finally {
            _lock.Release();
        }
    }
}

/// <summary>Cancels a running command after a time limit or when the caller goes away.</summary>
public sealed class QueryTimer : IDisposable {
    private readonly Timer _timer;
    private readonly CancellationTokenRegistration _registration;

    public const string TimedOutItem = "dp.timedOut";

    public QueryTimer(DuckDBCommand command, int seconds, CancellationToken cancellationToken, HttpContext? context) {
        _timer = new Timer(_ => {
            if (context != null) {
                context.Items[TimedOutItem] = true;
            }
            Cancel(command);
        }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        _registration = cancellationToken.Register(() => Cancel(command));
    }

    private static void Cancel(DuckDBCommand command) {
        try {
            command.Cancel();
        } catch (Exception) {
            // The command may have just finished; there is nothing left to cancel.
        }
    }

    public void Dispose() {
        _timer.Dispose();
        _registration.Dispose();
    }
}
