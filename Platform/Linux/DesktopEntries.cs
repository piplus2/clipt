using Avalonia.Media.Imaging;

namespace Clipt.Platform.Linux;

/// <summary>
/// Installed apps from freedesktop .desktop files: their familiar names and icons, indexed by the executable
/// file name a running process reports (/proc/PID/exe), so "/usr/lib/firefox/firefox" shows as "Firefox".
/// </summary>
internal sealed class DesktopEntries
{
    public sealed record Entry(string Name, string Exe, string? ExePath, string? Icon, string Id);

    private static readonly Lazy<DesktopEntries> Instance = new(() => new DesktopEntries());
    public static DesktopEntries Current => Instance.Value;

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Entry> _byExe = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Entry> All => _entries;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IEnumerable<string> DataDirs()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        yield return string.IsNullOrEmpty(dataHome) ? Path.Combine(Home, ".local/share") : dataHome;
        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var d in (string.IsNullOrEmpty(dirs) ? "/usr/local/share:/usr/share" : dirs).Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return d;
        yield return Path.Combine(Home, ".local/share/flatpak/exports/share");
        yield return "/var/lib/flatpak/exports/share";
        yield return "/var/lib/snapd/desktop";
    }

    private DesktopEntries()
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in DataDirs().Distinct().Select(d => Path.Combine(d, "applications")).Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var file in files)
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!seenIds.Add(id)) continue; // earlier data dirs override later ones
                if (Parse(file, id) is { } e)
                {
                    _entries.Add(e);
                    _byExe.TryAdd(e.Exe, e);
                }
            }
        }
    }

    private static Entry? Parse(string file, string id)
    {
        string? name = null, exec = null, icon = null, type = null, wmClass = null, flatpak = null;
        bool hidden = false, inMain = false;
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    if (inMain) break;
                    inMain = line == "[Desktop Entry]";
                    continue;
                }
                if (!inMain) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "Name": name = value; break;
                    case "Exec": exec = value; break;
                    case "Icon": icon = value; break;
                    case "Type": type = value; break;
                    case "StartupWMClass": wmClass = value; break;
                    case "X-Flatpak": flatpak = value; break;
                    case "NoDisplay" or "Hidden" when value.Equals("true", StringComparison.OrdinalIgnoreCase): hidden = true; break;
                }
            }
        }
        catch { return null; }
        if (hidden || type != "Application" || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exec)) return null;

        var (exe, path) = ResolveExec(exec, flatpak, wmClass);
        return exe is null ? null : new Entry(name, exe, path, icon, id);
    }

    /// <summary>The program a desktop entry starts, as a process would report it.</summary>
    private static (string? exe, string? path) ResolveExec(string exec, string? flatpakId, string? wmClass)
    {
        var args = SplitExec(exec);
        // Skip "env VAR=value …" wrappers.
        int i = 0;
        if (i < args.Count && Path.GetFileName(args[i]) == "env")
        {
            i++;
            while (i < args.Count && args[i].Contains('=')) i++;
        }
        if (i >= args.Count) return (null, null);

        // Flatpak apps run sandboxed; their binary is usually named after the last part of the app id.
        if (flatpakId is not null || Path.GetFileName(args[i]) == "flatpak")
        {
            var appId = flatpakId ?? args.Skip(i).LastOrDefault(a => a.Count(c => c == '.') >= 2 && !a.StartsWith('-'));
            var guess = wmClass ?? appId?.Split('.').LastOrDefault();
            return (guess?.ToLowerInvariant(), null);
        }

        var program = args[i];
        var path = program.Contains('/') ? program : FindOnPath(program);
        if (path is not null)
        {
            try
            {
                // /usr/bin/foo is often a symlink to the real binary the process will report.
                var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
                if (target is { Exists: true }) path = target.FullName;
            }
            catch { }
        }
        return (Path.GetFileName(path ?? program), path);
    }

    private static List<string> SplitExec(string exec)
    {
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (c == '\\' && i + 1 < exec.Length) { cur.Append(exec[++i]); continue; }
            if (c == ' ' && !quoted)
            {
                if (cur.Length > 0) { args.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) args.Add(cur.ToString());
        return args.Where(a => !(a.Length == 2 && a[0] == '%')).ToList(); // drop %U, %f…
    }

    public static string? FindOnPath(string program)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, program);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public Entry? ForExecutable(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var exe = Path.GetFileName(path);
        if (_byExe.TryGetValue(exe, out var e)) return e;
        // Wrapper names such as "firefox-bin" or "code-insiders".
        return _entries.FirstOrDefault(x => exe.StartsWith(x.Exe + "-", StringComparison.OrdinalIgnoreCase) ||
                                            exe.StartsWith(x.Exe + "_", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- icons ----------

    private static readonly string[] IconSizes = ["48x48", "64x64", "32x32", "128x128", "256x256", "96x96", "24x24", "512x512"];

    public static Bitmap? LoadIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        string? file = null;
        if (Path.IsPathRooted(icon)) file = File.Exists(icon) && icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? icon : null;
        else
        {
            var themes = new[] { CurrentIconTheme(), "hicolor" }.Where(t => t is not null).Distinct();
            foreach (var root in DataDirs().Select(d => Path.Combine(d, "icons")).Prepend(Path.Combine(Home, ".icons")))
            {
                foreach (var theme in themes)
                foreach (var size in IconSizes)
                {
                    var candidate = Path.Combine(root, theme!, size, "apps", icon + ".png");
                    if (File.Exists(candidate)) { file = candidate; break; }
                }
                if (file is not null) break;
            }
            file ??= new[] { $"/usr/share/pixmaps/{icon}.png" }.FirstOrDefault(File.Exists);
        }
        if (file is null) return null;
        try
        {
            using var s = File.OpenRead(file);
            return Bitmap.DecodeToWidth(s, 48);
        }
        catch { return null; }
    }

    private static string? _theme;
    private static bool _themeRead;

    private static string? CurrentIconTheme()
    {
        if (_themeRead) return _theme;
        _themeRead = true;
        _theme = LinuxShell.RunAndRead("gsettings", "get", "org.gnome.desktop.interface", "icon-theme")?.Trim().Trim('\'');
        return _theme;
    }
}
