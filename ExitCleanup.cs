namespace DataPuddle;

/// <summary>
/// Removes the local database and Delta files when the program ends, so cloned data does not linger on disk.
/// It hooks process exit, which covers a normal finish, quitting the shell, stopping the API and Ctrl+C, and
/// it runs after everything else, including programs started by pipeline actions. A crash, a killed process
/// or a power loss cannot be covered. Safe to run more than once.
/// </summary>
internal sealed class ExitCleanup {
    private readonly LocalStore _store;
    private int _done;

    public ExitCleanup(LocalStore store) {
        _store = store;
    }

    public void Register() {
        AppDomain.CurrentDomain.ProcessExit += (object? sender, EventArgs e) => Run();
    }

    public void Run() {
        if (Interlocked.Exchange(ref _done, 1) != 0) {
            return;
        }

        try {
            _store.Dispose();
        } catch (Exception) {
            // Closing is best effort; the deletion below reports anything that is still locked.
        }

        IReadOnlyList<string> failures = _store.DeleteLocalData();
        if (failures.Count == 0) {
            Cli.Line("Deleted the local database and Delta files (DeleteDataOnExit).", "grey");
        } else {
            Cli.Warning("Could not delete: " + string.Join(", ", failures) + ". Remove them manually.");
        }
    }
}
