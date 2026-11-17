using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clipt.Services;

namespace Clipt.Platform.Linux;

/// <summary>Linux (X11, including XWayland) implementation of <see cref="IPlatform"/>.</summary>
internal sealed class LinuxPlatform : IPlatform
{
    private readonly X11Session _x;
    private readonly SecretServiceKeyProtector _keys;
    private readonly Lazy<IOcrEngine?> _ocr = new(TesseractOcr.TryCreate);

    public LinuxPlatform(string dataDir)
    {
        _x = new X11Session();
        _keys = new SecretServiceKeyProtector(dataDir);
        _x.ForeignClipboardChange += () => Dispatcher.UIThread.Post(() => ClipboardChanged?.Invoke());
        _x.HotkeyPressed += () => Dispatcher.UIThread.Post(() => HotkeyPressed?.Invoke());
    }

    public string OsName => "Linux";
    public void Start()
    {
        if (!_x.HasXFixes)
            App.Log(new InvalidOperationException("The X server lacks the XFixes extension; clipboard changes can't be detected."));
    }

    // ---------- clipboard ----------

    public event Action? ClipboardChanged;

    public Task<ClipboardContent?> ReadClipboardAsync(bool includeImage) => _x.ReadClipboardAsync(includeImage);

    public long WriteClipboard(ClipboardContent content) => _x.SetClipboard(content);

    public bool IsClipboardStill(long token) => _x.OwnsClipboardWith(token);

    public void ClearClipboard() => _x.ClearClipboard();

    public SourceApp GetClipboardSource()
    {
        int pid = _x.WindowPid(_x.ClipboardOwner);
        if (pid <= 0) pid = _x.WindowPid(_x.ActiveWindow());
        if (pid <= 0) return default;
        var path = ProcessPath(pid);
        var name = FriendlyAppName(path ?? ProcessName(pid) ?? "", null);
        return new SourceApp(pid, string.IsNullOrEmpty(name) ? null : name, path);
    }

    private static string? ProcessPath(int pid)
    {
        try { return new FileInfo($"/proc/{pid}/exe").LinkTarget?.Replace(" (deleted)", ""); }
        catch { return null; }
    }

    private static string? ProcessName(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); }
        catch { return null; }
    }

    private static readonly HashSet<string> Keepers = new(StringComparer.OrdinalIgnoreCase)
    {
        "gnome-shell", "gsd-clipboard", "plasmashell", "klipper", "xfce4-clipman", "clipit", "parcellite", "diodon",
        "copyq", "gpaste-daemon", "cinnamon", "muffin", "budgie-wm", "mate-settings-daemon", "lxqt-panel", "clipmenud",
    };

    public bool IsClipboardKeeper(SourceApp source) =>
        source.Path is { } p && Keepers.Contains(Path.GetFileName(p));

    public string? GetActiveWindowTitle() => _x.WindowTitle(_x.ActiveWindow());

    // ---------- hotkey, focus and paste ----------

    public event Action? HotkeyPressed;

    public bool RegisterHotkey(Hotkey hotkey)
    {
        uint? keysym = Keysym(hotkey.Key);
        if (keysym is null) return false;
        uint mods = 0;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= Xlib.ShiftMask;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Control)) mods |= Xlib.ControlMask;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= Xlib.Mod1Mask;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= Xlib.Mod4Mask;
        return _x.GrabHotkey(keysym.Value, mods);
    }

    public bool SupportsWinVTakeover => false;
    public bool WinVTakeover { get => false; set { } }
#pragma warning disable CS0067 // never raised on Linux
    public event Action? WinVPressed;
    public event Action? SessionLocked;
#pragma warning restore CS0067

    public nint GetForegroundWindow() => _x.ActiveWindow();

    public PixelPoint? GetAnchorPoint(nint targetWindow)
    {
        var p = _x.WindowCenter(targetWindow) ?? _x.Pointer();
        return p is { } v ? new PixelPoint(v.x, v.y) : null;
    }

    public void ForceForeground(TopLevel window)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } h && h != 0) _x.Activate(h);
    }

    private static readonly string[] Terminals =
    [
        "gnome-terminal", "gnome-terminal-server", "org.gnome.console", "kgx", "konsole", "xterm", "uxterm", "kitty", "alacritty",
        "tilix", "terminator", "xfce4-terminal", "mate-terminal", "lxterminal", "qterminal", "foot", "wezterm",
        "org.wezfurlong.wezterm", "ghostty", "com.mitchellh.ghostty", "guake", "yakuake", "tilda", "st", "st-256color",
        "urxvt", "rxvt", "terminology", "cool-retro-term", "blackbox", "com.raggesilver.blackbox", "ptyxis", "org.gnome.ptyxis",
    ];

    public async Task SendPasteAsync(nint target)
    {
        if (target == 0) return;
        bool terminal = _x.WindowClass(target) is { } cls && Terminals.Contains(cls.ToLowerInvariant());

        _x.Activate(target);
        for (int i = 0; i < 25 && _x.ActiveWindow() != target; i++)
            await Task.Delay(15);

        // Wait for the user to let go of Shift/Alt/Super (Shift+Enter, Alt+1…), or the target would get another shortcut.
        for (int i = 0; i < 60 && _x.ModifiersHeld(); i++)
            await Task.Delay(15);
        await Task.Delay(30);
        _x.SendPasteKeys(terminal);
    }

    // ---------- windows ----------

    public void PrepareLauncherWindow(Window window) { }

    /// <summary>Freedesktop has no reliable accent colour yet (GNOME 47+ only); Clipt keeps its own.</summary>
    public Avalonia.Media.Color? GetAccentColor(bool dark) => null;

    public bool SupportsCaptureProtection => false;
    public void SetCaptureProtection(Window window, bool enabled) { }

    public void TrimWorkingSet()
    {
        try { Libc.malloc_trim(0); } catch { /* not glibc */ }
    }

    // ---------- shell ----------

    public string StartupLabel => "Start at login";

    public void ApplyStartup(bool enabled)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var dir = Path.Combine(string.IsNullOrEmpty(config) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : config, "autostart");
        var file = Path.Combine(dir, "clipt.desktop");
        if (!enabled)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        // An AppImage runs from a temporary mount; autostart the AppImage file itself.
        var exe = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;
        if (exe is null) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, $"""
            [Desktop Entry]
            Type=Application
            Name=Clipt
            Comment=Clipboard history
            Exec="{exe.Replace("\\", "\\\\").Replace("\"", "\\\"")}" --background
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """ + "\n");
    }

    public void Open(string target) => LinuxShell.Start("xdg-open", target);

    public void Reveal(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        // The FileManager1 D-Bus interface (Nautilus, Dolphin, Nemo, Thunar…) selects the file without opening it.
        bool shown = LinuxShell.RunAndRead("dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
            "--type=method_call", "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
            $"array:string:{uri}", "string:") is not null;
        if (!shown)
        {
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (folder is not null) Open(folder);
        }
    }

    public ITray CreateTray(App app) => new LinuxTray(app, UI.IconFactory.AppImage, UI.IconFactory.Png(64));

    // ---------- apps ----------

    public IEnumerable<(string? name, string path, AppOrigin origin)> EnumerateApps()
    {
        var seen = new HashSet<int>();
        foreach (var pid in Directory.EnumerateDirectories("/proc").Select(d => int.TryParse(Path.GetFileName(d), out var p) ? p : 0))
        {
            if (pid <= 0 || pid == Environment.ProcessId || !seen.Add(pid)) continue;
            var path = ProcessPath(pid);
            // Only apps the user can see: those with a desktop entry.
            if (path is not null && DesktopEntries.Current.ForExecutable(path) is { } entry)
                yield return (entry.Name, path, AppOrigin.Running);
        }
        foreach (var e in DesktopEntries.Current.All)
            yield return (e.Name, e.ExePath ?? e.Exe, AppOrigin.Installed);
    }

    public string FriendlyAppName(string path, string? hint)
    {
        if (DesktopEntries.Current.ForExecutable(path) is { } e) return e.Name;
        if (!string.IsNullOrWhiteSpace(hint)) return hint.Trim();
        var name = Path.GetFileName(path);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    public Bitmap? LoadAppIcon(string path) => DesktopEntries.LoadIcon(DesktopEntries.Current.ForExecutable(path)?.Icon);

    public (string name, string[] patterns)? ProgramFileFilter => null;

    // ---------- security & OCR ----------

    public IKeyProtector KeyProtector => _keys;

    public string KeyProtectionDescription => _keys.UsesKeyring
        ? "Your history is encrypted (AES-256) with a key kept in your desktop keyring, unlocked when you log in."
        : "Your history is encrypted (AES-256). No desktop keyring was found, so its key is kept in a file only your account can read.";

    public IOcrEngine? Ocr => _ocr.Value;

    public string OcrDescription => _ocr.Value is not null
        ? "Uses Tesseract on your computer. Nothing is uploaded."
        : "Needs Tesseract (e.g. sudo apt install tesseract-ocr). Runs on your computer, nothing is uploaded.";

    public void Dispose() => _x.Dispose();

    // ---------- keys ----------

    /// <summary>X keysym for an Avalonia key (the subset usable in a global shortcut).</summary>
    internal static uint? Keysym(Key k) => k switch
    {
        >= Key.A and <= Key.Z => (uint)('a' + (k - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)('0' + (k - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => 0xffb0 + (uint)(k - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0xffbe + (uint)(k - Key.F1),
        Key.Space => 0x20,
        Key.OemComma => ',',
        Key.OemPeriod => '.',
        Key.OemQuestion => '/',
        Key.OemSemicolon => ';',
        Key.OemQuotes => '\'',
        Key.OemOpenBrackets => '[',
        Key.OemCloseBrackets => ']',
        Key.OemMinus => '-',
        Key.OemPlus => '=',
        Key.Oem3 => '`',
        Key.Oem5 => '\\',
        Key.Insert => 0xff63,
        Key.Delete => 0xffff,
        Key.Home => 0xff50,
        Key.End => 0xff57,
        Key.PageUp => 0xff55,
        Key.PageDown => 0xff56,
        Key.Left => 0xff51,
        Key.Up => 0xff52,
        Key.Right => 0xff53,
        Key.Down => 0xff54,
        Key.Enter => 0xff0d,
        Key.Tab => 0xff09,
        Key.Escape => 0xff1b,
        Key.Back => 0xff08,
        Key.Pause => 0xff13,
        Key.PrintScreen => 0xff61,
        _ => null,
    };
}
