namespace DataPuddle.Plugins;

/// <summary>The kind of value a plugin function reads and returns.</summary>
public enum PluginValueKind {
    String,
    Integer,
    Double
}

/// <summary>A SQL function offered by a plugin.</summary>
public sealed class PluginFunction {
    internal PluginFunction(string pluginName, string name, PluginValueKind kind, int arity, string? description, Func<object?[], object?> invoke) {
        PluginName = pluginName;
        Name = name;
        Kind = kind;
        Arity = arity;
        Description = description;
        Invoke = invoke;
    }

    public string PluginName { get; }
    public string Name { get; }
    public PluginValueKind Kind { get; }
    public int Arity { get; }
    public string? Description { get; }

    /// <summary>True once the function has been registered with DuckDB.</summary>
    public bool Registered { get; internal set; }

    internal Func<object?[], object?> Invoke { get; }

    /// <summary>For example "mask_value(string) -> string".</summary>
    public string Signature {
        get {
            string type = Kind.ToString().ToLowerInvariant();
            return $"{Name}({string.Join(", ", Enumerable.Repeat(type, Arity))}) -> {type}";
        }
    }
}

/// <summary>A plugin that was found and started.</summary>
public sealed class LoadedPlugin {
    public LoadedPlugin(string name, string description, string assemblyPath, string version) {
        Name = name;
        Description = description;
        AssemblyPath = assemblyPath;
        Version = version;
    }

    public string Name { get; }
    public string Description { get; }
    public string AssemblyPath { get; }
    public string Version { get; }
}
