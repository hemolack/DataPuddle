using System.Reflection;
using System.Runtime.Loader;

namespace DataPuddle.Plugins;

/// <summary>
/// Loads one plugin DLL and the libraries it ships with (found through its .deps.json when present).
/// DataPuddle itself and any assembly the program has already loaded are shared, so the plugin and the
/// host agree on the types in <see cref="IPuddlePlugin"/>.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext {
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base("plugin:" + Path.GetFileName(pluginPath), isCollectible: false) {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName) {
        // Returning null lets the default context supply it.
        if (string.Equals(assemblyName.Name, typeof(PluginLoadContext).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        foreach (Assembly loaded in AssemblyLoadContext.Default.Assemblies) {
            if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)) {
                return loaded;
            }
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
