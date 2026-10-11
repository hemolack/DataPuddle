using Microsoft.Extensions.Configuration;

namespace DataPuddle.Plugins;

/// <summary>The "Plugins" section of the configuration.</summary>
public sealed class PluginOptions {
    /// <summary>Set to false to load no plugins at all. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Extra folders to search, besides the current directory, the program's own folder and the
    /// "plugins" subfolder of each. Relative paths are resolved against the current directory.
    /// </summary>
    public IReadOnlyList<string> Directories { get; init; } = Array.Empty<string>();

    /// <summary>Plugin names (see <see cref="IPuddlePlugin.Name"/>) that are found but not started.</summary>
    public IReadOnlyList<string> Disabled { get; init; } = Array.Empty<string>();

    /// <summary>Settings per plugin name, each flattened to "key" or "parent:child" paths.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Settings { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the section. Throws <see cref="FormatException"/> for an invalid value.</summary>
    public static PluginOptions Load(IConfigurationSection section) {
        bool enabled = true;
        string? enabledText = section["Enabled"];
        if (!string.IsNullOrWhiteSpace(enabledText) && !bool.TryParse(enabledText, out enabled)) {
            throw new FormatException($"\"Plugins:Enabled\" must be true or false, but was '{enabledText}'.");
        }

        List<string> directories = ReadList(section.GetSection("Directories"));
        List<string> disabled = ReadList(section.GetSection("Disabled"));

        Dictionary<string, IReadOnlyDictionary<string, string>> settings =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection plugin in section.GetSection("Settings").GetChildren()) {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Flatten(plugin, string.Empty, values);
            settings[plugin.Key] = values;
        }

        return new PluginOptions {
            Enabled = enabled,
            Directories = directories,
            Disabled = disabled,
            Settings = settings
        };
    }

    private static List<string> ReadList(IConfigurationSection section) {
        List<string> items = new List<string>();
        foreach (IConfigurationSection child in section.GetChildren()) {
            if (!string.IsNullOrWhiteSpace(child.Value)) {
                items.Add(child.Value.Trim());
            }
        }
        return items;
    }

    private static void Flatten(IConfigurationSection section, string prefix, Dictionary<string, string> target) {
        foreach (IConfigurationSection child in section.GetChildren()) {
            string key = prefix.Length == 0 ? child.Key : prefix + ":" + child.Key;
            if (child.Value != null) {
                target[key] = child.Value;
            }
            Flatten(child, key, target);
        }
    }
}
