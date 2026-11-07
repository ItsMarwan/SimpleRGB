namespace SimpleRGB.Core;

/// <summary>Well-known folders. Everything SimpleRGB writes lives under %APPDATA%\SimpleRGB.</summary>
public static class AppPaths
{
    private static string? _overrideRoot;

    /// <summary>Lets tests redirect all app data into a temp folder.</summary>
    public static void OverrideDataRoot(string? root) => _overrideRoot = root;

    public static string DataDir => Ensure(_overrideRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleRGB"));

    public static string LogDir => Ensure(Path.Combine(DataDir, "logs"));
    public static string PluginsDir => Ensure(Path.Combine(DataDir, "Plugins"));

    /// <summary>Plugins the user drops in by hand. Highest priority.</summary>
    public static string CustomPluginsDir => Ensure(Path.Combine(PluginsDir, "Custom"));

    /// <summary>Plugins copied from a local SignalRGB installation.</summary>
    public static string SignalRgbPluginsDir => Ensure(Path.Combine(PluginsDir, "SignalRGB"));

    /// <summary>Plugins downloaded from the public SignalRGB GitLab repository.</summary>
    public static string GitLabPluginsDir => Ensure(Path.Combine(PluginsDir, "GitLab"));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string PluginCacheFile => Path.Combine(DataDir, "plugin-cache.json");

    /// <summary>SimpleRGB's own plugins, shipped next to the exe (written by us, not from SignalRGB).</summary>
    public static string BuiltInPluginsDir => Path.Combine(AppContext.BaseDirectory, "Plugins");

    /// <summary>Folder that holds the bundled, signed PawnIO modules.</summary>
    public static string PawnIoModulesDir => Path.Combine(AppContext.BaseDirectory, "PawnIO");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
