using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SimpleRGB.Core.Diagnostics;

namespace SimpleRGB.Core.Plugins;

/// <summary>
/// Finds plugin files, keeps local copies up to date and caches their metadata.
/// Priority when the same file exists in several places: Custom &gt; SignalRGB &gt; GitLab.
/// Plugins are never bundled with SimpleRGB; they come from the user's own SignalRGB install or are
/// downloaded from SignalRGB's public GitLab repository on the user's machine.
/// </summary>
public sealed partial class PluginLibrary
{
    public const string GitLabArchiveUrl = "https://gitlab.com/signalrgb/signal-plugins/-/archive/master/signal-plugins-master.zip";
    private const int CacheVersion = 4;

    private readonly object _sync = new();
    private Dictionary<string, CachedMeta> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheDirty;

    public IReadOnlyList<(string Name, string Dir)> Roots { get; }

    public PluginLibrary(IReadOnlyList<(string Name, string Dir)>? roots = null)
    {
        Roots = roots ?? new[]
        {
            ("Custom", AppPaths.CustomPluginsDir),
            ("SimpleRGB", AppPaths.BuiltInPluginsDir),
            ("SignalRGB", AppPaths.SignalRgbPluginsDir),
            ("GitLab", AppPaths.GitLabPluginsDir),
        };
        LoadCache();
    }

    // ---------------------------------------------------------------- sources

    /// <summary>SignalRGB plugin folders on this PC, oldest first (later ones overwrite earlier ones).</summary>
    public static List<string> FindSignalRgbPluginFolders()
    {
        var result = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var vortx = Path.Combine(local, "VortxEngine");
        if (Directory.Exists(vortx))
        {
            var newest = Directory.GetDirectories(vortx, "app-*")
                .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : new Version(0, 0)))
                .OrderByDescending(x => x.Version)
                .Select(x => Path.Combine(x.Dir, "Signal-x64", "Plugins"))
                .FirstOrDefault(Directory.Exists);
            if (newest != null) result.Add(newest);
        }

        // SignalRGB's plugin CDN cache holds the newest versions.
        var cdn = Path.Combine(local, "WhirlwindFX", "SignalRgb", "cache", "plugin_cdn", "main", "Plugins");
        if (Directory.Exists(cdn)) result.Add(cdn);
        return result;
    }

    /// <summary>Copies plugin .js files from a local SignalRGB install. Returns the number of changed files.</summary>
    public int ImportFromSignalRgb()
    {
        var target = Roots.First(r => r.Name == "SignalRGB").Dir;
        var changed = 0;
        // Resolve the winning copy of each file first (later folders = newer), then copy once.
        var winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in FindSignalRgbPluginFolders())
            foreach (var file in Directory.EnumerateFiles(folder, "*.js", SearchOption.AllDirectories))
                winners[Path.GetRelativePath(folder, file)] = file;

        {
            foreach (var (rel, file) in winners)
            {
                try
                {
                    var dest = Path.Combine(target, rel);
                    var src = new FileInfo(file);
                    var dst = new FileInfo(dest);
                    // File.Copy keeps the timestamp, so size + time identifies an unchanged copy cheaply.
                    if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) continue;
                    if (dst.Exists && dst.Length == src.Length && FilesEqual(src.FullName, dst.FullName)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                    changed++;
                }
                catch (Exception ex)
                {
                    Log.Warn("Plugins", $"Could not copy {file}: {ex.Message}");
                }
            }
        }
        if (changed > 0) Log.Info("Plugins", $"Imported {changed} updated plugin file(s) from SignalRGB.");
        return changed;
    }

    /// <summary>Downloads the public SignalRGB plugin repository into the GitLab folder.</summary>
    public async Task<int> DownloadFromGitLabAsync(CancellationToken ct = default)
    {
        var target = Path.GetFullPath(Roots.First(r => r.Name == "GitLab").Dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleRGB/1.0");
        var tmp = Path.Combine(Path.GetTempPath(), $"simplergb-plugins-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var stream = await http.GetStreamAsync(GitLabArchiveUrl, ct))
            await using (var file = File.Create(tmp))
                await stream.CopyToAsync(file, ct);

            var count = 0;
            using var zip = ZipFile.OpenRead(tmp);
            foreach (var entry in zip.Entries)
            {
                // Archive layout: signal-plugins-master/Plugins/<Brand>/<file>.js
                var parts = entry.FullName.Split('/', 3);
                if (parts.Length < 3 || parts[1] != "Plugins" || !entry.FullName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    continue;
                var dest = Path.GetFullPath(Path.Combine(target, parts[2]));
                if (!dest.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                count++;
            }
            Log.Info("Plugins", $"Downloaded {count} plugin file(s) from GitLab.");
            return count;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        try { return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)); }
        catch { return false; }
    }

    /// <summary>All plugin files that would be used, after applying root priority.</summary>
    public List<(string Source, string Root, string RelativePath, string FullPath)> EffectiveFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string, string, string)>();
        foreach (var (name, dir) in Roots)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (rel.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(rel)) list.Add((name, dir, rel, file));
            }
        }
        return list;
    }

    public int Count => EffectiveFiles().Count;

    // ---------------------------------------------------------------- metadata

    [GeneratedRegex(@"function\s+Type\s*\(\s*\)\s*\{\s*return\s*[""'](\w+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex TypeRegex();

    /// <summary>Plugins that might drive one of the given USB vendor ids, plus every SMBus plugin.</summary>
    public (List<PluginInfo> Hid, List<PluginInfo> SmBus) Discover(IReadOnlyCollection<int> connectedVendorIds)
    {
        var vendorTokens = connectedVendorIds.Distinct()
            .Select(v => $"0x{v:x4}")
            .ToArray();

        var hid = new List<PluginInfo>();
        var smbus = new List<PluginInfo>();
        foreach (var (source, root, rel, full) in EffectiveFiles())
        {
            string text;
            FileInfo fi;
            try
            {
                fi = new FileInfo(full);
                text = File.ReadAllText(full);
            }
            catch { continue; }

            var typeMatch = TypeRegex().Match(text);
            var quickType = typeMatch.Success ? typeMatch.Groups[1].Value.ToLowerInvariant() : "";
            if (quickType is "rawusb" or "serial" or "network" or "hybrid") continue;

            var isSmBus = quickType == "smbus";
            if (!isSmBus)
            {
                // Cheap prefilter: only evaluate plugins that mention a connected vendor id.
                if (vendorTokens.Length == 0 || !vendorTokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            var info = GetMetadata(source, root, rel, full, fi);
            if (info.LoadError != null) continue;
            if (info.Kind == PluginKind.SmBus) smbus.Add(info);
            else if (info.Kind == PluginKind.Hid) hid.Add(info);
        }
        SaveCache();
        return (hid, smbus);
    }

    private PluginInfo GetMetadata(string source, string root, string rel, string full, FileInfo fi)
    {
        var stamp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        lock (_sync)
        {
            if (_cache.TryGetValue(full, out var cached) && cached.Stamp == stamp)
                return cached.ToInfo(source, root, rel, full);
        }

        var info = new PluginInfo { FilePath = full, RelativePath = rel, RootDir = root, Source = source };
        try
        {
            using var runtime = PluginRuntime.LoadForMetadata(info);
            var rawType = runtime.GetString("Type");
            var kind = rawType.ToLowerInvariant() switch
            {
                "hid" => PluginKind.Hid,
                "smbus" => PluginKind.SmBus,
                "" => PluginKind.Unknown,
                _ => PluginKind.Other,
            };
            info = info with
            {
                Name = runtime.GetString("Name"),
                Publisher = runtime.GetString("Publisher"),
                RawType = rawType,
                Kind = kind,
                VendorIds = runtime.GetIntList("VendorId"),
                ProductIds = runtime.GetIntList("ProductId"),
            };
        }
        catch (Exception ex)
        {
            info = info with { LoadError = ex.Message };
            Log.Debug("Plugins", $"Skipping {rel}: {ex.Message}");
        }

        lock (_sync)
        {
            _cache[full] = CachedMeta.From(info, stamp);
            _cacheDirty = true;
        }
        return info;
    }

    private sealed record CachedMeta(string Stamp, string Name, string Publisher, string RawType, PluginKind Kind,
        int[] VendorIds, int[] ProductIds, string? LoadError)
    {
        public static CachedMeta From(PluginInfo i, string stamp) =>
            new(stamp, i.Name, i.Publisher, i.RawType, i.Kind, i.VendorIds.ToArray(), i.ProductIds.ToArray(), i.LoadError);

        public PluginInfo ToInfo(string source, string root, string rel, string full) => new()
        {
            FilePath = full, RelativePath = rel, RootDir = root, Source = source,
            Name = Name, Publisher = Publisher, RawType = RawType, Kind = Kind,
            VendorIds = VendorIds, ProductIds = ProductIds, LoadError = LoadError,
        };
    }

    private sealed record CacheFile(int Version, Dictionary<string, CachedMeta> Entries);

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.PluginCacheFile)) return;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(AppPaths.PluginCacheFile));
            if (file?.Version == CacheVersion && file.Entries != null)
                _cache = new Dictionary<string, CachedMeta>(file.Entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Plugins", $"Plugin cache unreadable, rebuilding ({ex.Message}).");
        }
    }

    private void SaveCache()
    {
        lock (_sync)
        {
            if (!_cacheDirty) return;
            try
            {
                var json = JsonSerializer.Serialize(new CacheFile(CacheVersion, _cache));
                var tmp = AppPaths.PluginCacheFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, AppPaths.PluginCacheFile, overwrite: true);
                _cacheDirty = false;
            }
            catch (Exception ex)
            {
                Log.Warn("Plugins", $"Could not save plugin cache: {ex.Message}");
            }
        }
    }

    public static string Hex(int v) => "0x" + v.ToString("X4", CultureInfo.InvariantCulture);
}
