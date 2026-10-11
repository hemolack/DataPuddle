namespace DataPuddle.Plugins;

/// <summary>
/// Implement this in a class library that references DataPuddle, then drop the built DLL next to the
/// program (or in a "plugins" folder there). DataPuddle finds it at startup, creates one instance
/// through the public parameterless constructor and calls <see cref="Initialize"/>.
/// </summary>
public interface IPuddlePlugin {
    /// <summary>Short unique name. Also the key of the plugin's settings: Plugins:Settings:&lt;Name&gt;.</summary>
    string Name { get; }

    /// <summary>One line shown by the shell's .plugins command and the API's /plugins endpoint.</summary>
    string Description { get; }

    /// <summary>Called once at startup, before the database is opened. Register what the plugin offers here.</summary>
    void Initialize(IPluginContext context);
}

/// <summary>
/// What a plugin can see and do. It deliberately uses only plain .NET types so a plugin needs no
/// reference other than DataPuddle itself. New capabilities (shell commands, pipeline steps, ...) can be
/// added here later without breaking plugins that were built earlier.
/// </summary>
public interface IPluginContext {
    /// <summary>Folder DataPuddle was started from.</summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// The plugin's settings from Plugins:Settings:&lt;Name&gt; in the config file (or the matching
    /// environment variables, e.g. Plugins__Settings__Privacy__Key). Nested keys are joined with ':'.
    /// Lookups ignore case. Empty when nothing is configured.
    /// </summary>
    IReadOnlyDictionary<string, string> Settings { get; }

    /// <summary>Writes a line to the console, prefixed with the plugin name.</summary>
    void Log(string message);

    // ---------- SQL functions ----------
    // Registered functions can be used anywhere SQL runs: the shell, pipeline steps, and the API.
    // A SQL NULL arrives as null, and a null result becomes NULL. Names are letters, digits and
    // underscores, starting with a letter or underscore, and must not clash with an existing function.
    // Throwing makes the SQL statement fail with your message.

    void RegisterStringFunction(string name, Func<string?, string?> function, string? description = null);
    void RegisterStringFunction(string name, Func<string?, string?, string?> function, string? description = null);
    void RegisterStringFunction(string name, Func<string?, string?, string?, string?> function, string? description = null);

    void RegisterIntegerFunction(string name, Func<long?, long?> function, string? description = null);
    void RegisterIntegerFunction(string name, Func<long?, long?, long?> function, string? description = null);

    void RegisterDoubleFunction(string name, Func<double?, double?> function, string? description = null);
    void RegisterDoubleFunction(string name, Func<double?, double?, double?> function, string? description = null);
}
