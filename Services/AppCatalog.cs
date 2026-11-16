using System.Diagnostics;
using System.IO;
using System.Windows.Media;

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

    public ImageSource? Icon => AppIconCache.Get(Path);
}

/// <summary>
/// Finds apps by the names people know them by, so excluding one never requires knowing what an .exe is.
/// Sources, in order of relevance: apps the user actually copied from, apps running now, Start-menu apps.
/// </summary>
public static class AppCatalog
{
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
        "TextInputHost.exe", "SystemSettings.exe", "LockApp.exe", "Clipt.exe", "conhost.exe", "dwm.exe",
        "unins000.exe", "uninstall.exe", "setup.exe", "update.exe", "msiexec.exe", "rundll32.exe",
        "control.exe", "mmc.exe", "explorer.exe", "msconfig.exe", "regedit.exe", "MdSched.exe", "dfrgui.exe",
        "cleanmgr.exe", "RecoveryDrive.exe", "iscsicpl.exe", "odbcad32.exe", "msinfo32.exe", "perfmon.exe",
        "resmon.exe", "WFS.exe", "psr.exe", "narrator.exe", "osk.exe", "magnify.exe",
    };

    public static List<AppEntry> Collect(IEnumerable<(string name, string path)> recentSources)
    {
        var byExe = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string? name, AppOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            var exe = System.IO.Path.GetFileName(path);
            if (Noise.Contains(exe) || exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || byExe.ContainsKey(exe)) return;
            byExe[exe] = new AppEntry { Exe = exe, Name = FriendlyName(path, name), Path = path, Origin = origin };
        }

        foreach (var (name, path) in recentSources) Add(path, name, AppOrigin.RecentlyCopied);
        foreach (var (name, path) in RunningApps()) Add(path, name, AppOrigin.Running);
        foreach (var (name, path) in StartMenuApps()) Add(path, name, AppOrigin.Installed);

        return byExe.Values
            .OrderBy(a => a.Origin)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static AppEntry FromPath(string path) => new()
    {
        Exe = System.IO.Path.GetFileName(path),
        Name = FriendlyName(path, null),
        Path = path,
        Origin = AppOrigin.Browsed,
    };

    private static string FriendlyName(string path, string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && !hint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return hint.Trim();
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    private static IEnumerable<(string name, string path)> RunningApps()
    {
        foreach (var p in Process.GetProcesses())
        {
            string? path = null, title = null;
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                title = p.MainWindowTitle;
                path = p.MainModule?.FileName;
            }
            catch { /* elevated/protected process */ }
            finally { p.Dispose(); }
            if (path is not null && !string.IsNullOrEmpty(title)) yield return (null!, path);
        }
    }

    /// <summary>Resolves Start-menu shortcuts (.lnk) to their target programs; the shortcut name is the app's familiar name.</summary>
    private static IEnumerable<(string name, string path)> StartMenuApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) yield break;
        dynamic? shell = null;
        try { shell = Activator.CreateInstance(shellType); } catch { }
        if (shell is null) yield break;

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch { continue; }

            foreach (var lnk in links)
            {
                string? target = null;
                try { target = (string)shell.CreateShortcut(lnk).TargetPath; } catch { }
                if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                yield return (System.IO.Path.GetFileNameWithoutExtension(lnk), target);
            }
        }
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
    }
}
