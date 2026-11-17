using Avalonia.Media.Imaging;
using Clipt.Platform;

namespace Clipt.Services;

public enum AppOrigin { RecentlyCopied, Running, Installed, Browsed }

public sealed class AppEntry
{
    public required string Exe { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public AppOrigin Origin { get; init; }

    public string OriginLabel => Origin switch
    {
        AppOrigin.RecentlyCopied => "You copied from this",
        AppOrigin.Running => "Running now",
        AppOrigin.Installed => "Installed",
        _ => "",
    };

    public Bitmap? Icon => AppIconCache.Get(Path);
}

/// <summary>
/// Finds apps by the names people know them by, so excluding one never requires knowing what an executable is.
/// Sources, in order of relevance: apps the user actually copied from, apps running now, installed apps.
/// </summary>
public static class AppCatalog
{
    public static List<AppEntry> Collect(IPlatform platform, IEnumerable<(string name, string path)> recentSources)
    {
        var byExe = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string? name, AppOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var exe = AppSettings.FileName(path);
            if (exe.Length == 0 || exe.Equals("Clipt", StringComparison.OrdinalIgnoreCase) || byExe.ContainsKey(exe)) return;
            byExe[exe] = new AppEntry { Exe = exe, Name = platform.FriendlyAppName(path, name), Path = path, Origin = origin };
        }

        foreach (var (name, path) in recentSources) Add(path, name, AppOrigin.RecentlyCopied);
        foreach (var (name, path, origin) in platform.EnumerateApps()) Add(path, name, origin);

        return byExe.Values
            .OrderBy(a => a.Origin)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static AppEntry FromPath(IPlatform platform, string path) => new()
    {
        Exe = AppSettings.FileName(path),
        Name = platform.FriendlyAppName(path, null),
        Path = path,
        Origin = AppOrigin.Browsed,
    };
}

/// <summary>App icons by executable path, loaded once through the platform layer.</summary>
public static class AppIconCache
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set at startup; null (e.g. in tests) means no icons.</summary>
    public static IPlatform? Platform { get; set; }

    public static Bitmap? Get(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || Platform is null) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(exePath, out var cached)) return cached;
        }
        Bitmap? result = null;
        try { result = Platform.LoadAppIcon(exePath); } catch { /* no icon */ }
        lock (Cache) Cache[exePath] = result;
        return result;
    }
}
