using System.Text.RegularExpressions;

namespace DataPuddle.Plugins;

/// <summary>The <see cref="IPluginContext"/> handed to one plugin. Collects what the plugin registers.</summary>
internal sealed class PluginContext : IPluginContext {
    private static readonly Regex NamePattern = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly string _pluginName;
    private readonly Action<string> _log;
    private readonly List<PluginFunction> _functions = new List<PluginFunction>();

    public PluginContext(string pluginName, IReadOnlyDictionary<string, string> settings, Action<string> log) {
        _pluginName = pluginName;
        Settings = settings;
        _log = log;
        WorkingDirectory = Directory.GetCurrentDirectory();
    }

    public string WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string> Settings { get; }

    public IReadOnlyList<PluginFunction> Functions {
        get {
            return _functions;
        }
    }

    public void Log(string message) {
        _log($"[{_pluginName}] {message}");
    }

    public void RegisterStringFunction(string name, Func<string?, string?> function, string? description = null) {
        Add(name, PluginValueKind.String, 1, description, a => function((string?)a[0]));
    }

    public void RegisterStringFunction(string name, Func<string?, string?, string?> function, string? description = null) {
        Add(name, PluginValueKind.String, 2, description, a => function((string?)a[0], (string?)a[1]));
    }

    public void RegisterStringFunction(string name, Func<string?, string?, string?, string?> function, string? description = null) {
        Add(name, PluginValueKind.String, 3, description, a => function((string?)a[0], (string?)a[1], (string?)a[2]));
    }

    public void RegisterIntegerFunction(string name, Func<long?, long?> function, string? description = null) {
        Add(name, PluginValueKind.Integer, 1, description, a => function((long?)a[0]));
    }

    public void RegisterIntegerFunction(string name, Func<long?, long?, long?> function, string? description = null) {
        Add(name, PluginValueKind.Integer, 2, description, a => function((long?)a[0], (long?)a[1]));
    }

    public void RegisterDoubleFunction(string name, Func<double?, double?> function, string? description = null) {
        Add(name, PluginValueKind.Double, 1, description, a => function((double?)a[0]));
    }

    public void RegisterDoubleFunction(string name, Func<double?, double?, double?> function, string? description = null) {
        Add(name, PluginValueKind.Double, 2, description, a => function((double?)a[0], (double?)a[1]));
    }

    private void Add(string name, PluginValueKind kind, int arity, string? description, Func<object?[], object?> invoke) {
        if (string.IsNullOrWhiteSpace(name) || !NamePattern.IsMatch(name)) {
            throw new ArgumentException($"'{name}' is not a valid function name. Use letters, digits and underscores, starting with a letter or underscore.");
        }
        foreach (PluginFunction existing in _functions) {
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)) {
                throw new ArgumentException($"The plugin already registered a function named '{name}'.");
            }
        }
        _functions.Add(new PluginFunction(_pluginName, name, kind, arity, description, invoke));
    }
}
