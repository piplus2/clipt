using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Clipt.Platform.Linux;

/// <summary>Starts helper programs with argument lists (never through a shell).</summary>
internal static class LinuxShell
{
    public static bool Start(string program, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch { return false; }
    }

    /// <summary>Runs a program and returns its standard output, or null if it can't run or fails.</summary>
    public static string? RunAndRead(string program, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// OCR with the Tesseract command-line tool, if installed (e.g. <c>sudo apt install tesseract-ocr</c>).
/// The decrypted image is piped through standard input; nothing is written to disk.
/// </summary>
internal sealed class TesseractOcr : IOcrEngine
{
    private readonly string _exe;
    private readonly string _languages;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TesseractOcr(string exe, string languages)
    {
        _exe = exe;
        _languages = languages;
    }

    public static TesseractOcr? TryCreate()
    {
        var exe = DesktopEntries.FindOnPath("tesseract");
        if (exe is null) return null;
        var installed = (LinuxShell.RunAndRead(exe, "--list-langs") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("List of", StringComparison.Ordinal))
            .ToHashSet();
        if (installed.Count == 0) return null;
        // English plus the user's own language when its data is installed.
        var langs = new List<string>();
        if (installed.Contains("eng")) langs.Add("eng");
        var mine = CultureInfo.CurrentUICulture.ThreeLetterISOLanguageName;
        if (mine != "eng" && installed.Contains(mine)) langs.Add(mine);
        if (langs.Count == 0) langs.Add(installed.First());
        return new TesseractOcr(exe, string.Join('+', langs));
    }

    public async Task<string?> RecognizeAsync(byte[] png)
    {
        await _gate.WaitAsync();
        try
        {
            var psi = new ProcessStartInfo(_exe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "stdin", "stdout", "-l", _languages, "--psm", "3" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.StandardInput.BaseStream.WriteAsync(png);
            p.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0) return null;
            var lines = (await output).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipt OCR failed: {ex.Message}");
            return null;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Tray icon via StatusNotifierItem (Avalonia), notifications via the desktop's notification service.</summary>
internal sealed class LinuxTray : ITray
{
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _open;
    private readonly NativeMenuItem _pause;
    private readonly string? _iconFile;

    public LinuxTray(App app, Bitmap logo, byte[] logoPng)
    {
        _open = new NativeMenuItem("Open Clipt");
        _open.Click += (_, _) => app.ShowLauncher();
        _pause = new NativeMenuItem("Pause recording") { ToggleType = MenuItemToggleType.CheckBox };
        _pause.Click += (_, _) => app.TogglePause();
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => app.OpenSettings();
        var quit = new NativeMenuItem("Quit Clipt");
        quit.Click += (_, _) => app.Quit();

        var menu = new NativeMenu();
        menu.Items.Add(_open);
        menu.Items.Add(_pause);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        _icon = new TrayIcon { Icon = new WindowIcon(logo), ToolTipText = "Clipt", Menu = menu, IsVisible = true };
        _icon.Clicked += (_, _) => app.ShowLauncher();
        TrayIcon.SetIcons(app, [_icon]);

        try
        {
            var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var dir = Path.Combine(string.IsNullOrEmpty(cache) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache") : cache, "clipt");
            Directory.CreateDirectory(dir);
            _iconFile = Path.Combine(dir, "clipt.png");
            File.WriteAllBytes(_iconFile, logoPng);
        }
        catch { _iconFile = null; }
    }

    public void Update(string hotkey, bool paused)
    {
        _open.Header = $"Open Clipt    {hotkey}";
        _pause.IsChecked = paused;
        _icon.ToolTipText = paused ? "Clipt — paused" : $"Clipt — {hotkey}";
    }

    public void Notify(string title, string text)
    {
        var args = new List<string> { "--app-name=Clipt" };
        if (_iconFile is not null) args.Add("--icon=" + _iconFile);
        args.Add(title);
        args.Add(text);
        LinuxShell.Start("notify-send", [.. args]);
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
