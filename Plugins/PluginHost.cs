using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DuckDB.NET.Data;

namespace DataPuddle.Plugins;

/// <summary>
/// Finds plugin DLLs, starts them, and registers the SQL functions they offer on the DuckDB connection.
/// A DLL is only considered when it references this program's assembly, so the libraries sitting beside
/// the executable are never loaded as plugins.
/// </summary>
public sealed class PluginHost {
    private readonly List<LoadedPlugin> _plugins = new List<LoadedPlugin>();
    private readonly List<PluginFunction> _functions = new List<PluginFunction>();

    private PluginHost() {
    }

    public IReadOnlyList<LoadedPlugin> Plugins {
        get {
            return _plugins;
        }
    }

    public IReadOnlyList<PluginFunction> Functions {
        get {
            return _functions;
        }
    }

    /// <summary>A host with nothing loaded.</summary>
    public static PluginHost Empty() {
        return new PluginHost();
    }

    /// <summary>
    /// Searches for plugins and calls their Initialize methods. A plugin that fails is reported through
    /// <paramref name="log"/> and skipped; it never stops the others or the program.
    /// </summary>
    public static PluginHost Load(PluginOptions options, Action<string> log) {
        PluginHost host = new PluginHost();
        if (!options.Enabled) {
            return host;
        }

        HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> functionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in SearchDirectories(options)) {
            string[] files;
            try {
                files = Directory.GetFiles(directory, "*.dll");
            } catch (Exception ex) {
                log($"Plugins: cannot read {directory}: {ex.Message}");
                continue;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files) {
                if (!ReferencesThisProgram(file)) {
                    continue;
                }
                if (!seenFiles.Add(Path.GetFileName(file))) {
                    log($"Plugins: {file} ignored; a plugin file with the same name was already found.");
                    continue;
                }
                host.LoadFile(file, options, log, loadedNames, functionNames);
            }
        }
        return host;
    }

    /// <summary>Registers every plugin function on the connection. Failures are logged and skipped.</summary>
    public void Attach(DuckDBConnection connection, Action<string> log) {
        foreach (PluginFunction function in _functions) {
            try {
                DuckFunctionBinder.Register(connection, function);
                function.Registered = true;
            } catch (Exception ex) {
                log($"Plugin function {function.Name} ({function.PluginName}) was not registered: {ex.Message}");
            }
        }
    }

    /// <summary>Lines describing what is loaded, for the shell's .plugins command.</summary>
    public IReadOnlyList<string> Describe() {
        List<string> lines = new List<string>();
        if (_plugins.Count == 0) {
            lines.Add("No plugins loaded. Drop a plugin DLL next to the program (or in a 'plugins' folder) and restart.");
            return lines;
        }
        foreach (LoadedPlugin plugin in _plugins) {
            lines.Add($"{plugin.Name} {plugin.Version} - {plugin.Description}");
            lines.Add($"    {plugin.AssemblyPath}");
            foreach (PluginFunction function in _functions) {
                if (function.PluginName == plugin.Name) {
                    string note = function.Registered ? string.Empty : "  [not registered]";
                    string text = string.IsNullOrWhiteSpace(function.Description) ? string.Empty : "  " + function.Description;
                    lines.Add($"    {function.Signature}{text}{note}");
                }
            }
        }
        return lines;
    }

    private void LoadFile(string file, PluginOptions options, Action<string> log, HashSet<string> loadedNames, HashSet<string> functionNames) {
        Type[] types;
        Assembly assembly;
        try {
            PluginLoadContext context = new PluginLoadContext(file);
            assembly = context.LoadFromAssemblyPath(file);
            try {
                types = assembly.GetTypes();
            } catch (ReflectionTypeLoadException ex) {
                types = ex.Types.Where((Type? t) => t != null).Select((Type? t) => t!).ToArray();
            }
        } catch (Exception ex) {
            log($"Plugins: {Path.GetFileName(file)} could not be loaded: {ex.Message}");
            return;
        }

        foreach (Type type in types) {
            if (!type.IsClass || type.IsAbstract || !typeof(IPuddlePlugin).IsAssignableFrom(type)) {
                continue;
            }
            if (type.GetConstructor(Type.EmptyTypes) == null) {
                log($"Plugins: {type.FullName} in {Path.GetFileName(file)} needs a public parameterless constructor.");
                continue;
            }

            string name = type.Name;
            try {
                IPuddlePlugin plugin = (IPuddlePlugin)Activator.CreateInstance(type)!;
                name = plugin.Name;

                if (options.Disabled.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                    log($"Plugin {name} is disabled in the configuration.");
                    continue;
                }
                if (!loadedNames.Add(name)) {
                    log($"Plugins: a plugin named {name} is already loaded; {type.FullName} ignored.");
                    continue;
                }

                IReadOnlyDictionary<string, string> settings;
                if (!options.Settings.TryGetValue(name, out IReadOnlyDictionary<string, string>? found)) {
                    settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                } else {
                    settings = found;
                }

                PluginContext pluginContext = new PluginContext(name, settings, log);
                plugin.Initialize(pluginContext);

                int accepted = 0;
                foreach (PluginFunction function in pluginContext.Functions) {
                    if (functionNames.Add(function.Name)) {
                        _functions.Add(function);
                        accepted++;
                    } else {
                        log($"Plugin {name}: function {function.Name} skipped; another plugin already provides it.");
                    }
                }

                string version = assembly.GetName().Version?.ToString() ?? "?";
                _plugins.Add(new LoadedPlugin(name, plugin.Description, file, version));
                log($"Plugin loaded: {name} {version} ({accepted} function(s))");
            } catch (Exception ex) {
                log($"Plugin {name} failed to start and was skipped: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> SearchDirectories(PluginOptions options) {
        string current = Directory.GetCurrentDirectory();
        string program = AppContext.BaseDirectory;

        List<string> candidates = new List<string> {
            current,
            Path.Combine(current, "plugins"),
            program,
            Path.Combine(program, "plugins")
        };
        foreach (string configured in options.Directories) {
            candidates.Add(Path.GetFullPath(configured, current));
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in candidates) {
            string full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(full) && seen.Add(full)) {
                yield return full;
            }
        }
    }

    // Reads only the file's metadata, so nothing is loaded or run for files that are not plugins.
    private static bool ReferencesThisProgram(string path) {
        string hostName = typeof(PluginHost).Assembly.GetName().Name ?? "DataPuddle";
        try {
            using (FileStream stream = File.OpenRead(path))
            using (PEReader reader = new PEReader(stream)) {
                if (!reader.HasMetadata) {
                    return false;
                }
                MetadataReader metadata = reader.GetMetadataReader();
                foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences) {
                    string referenced = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                    if (string.Equals(referenced, hostName, StringComparison.OrdinalIgnoreCase)) {
                        return true;
                    }
                }
                return false;
            }
        } catch (Exception ex) when (ex is BadImageFormatException || ex is IOException || ex is UnauthorizedAccessException) {
            return false;
        }
    }
}
