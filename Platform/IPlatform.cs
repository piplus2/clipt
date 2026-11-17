using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Clipt.Services;

namespace Clipt.Platform;

/// <summary>What Clipt reads from, and puts on, the system clipboard. Only the formats Clipt cares about.</summary>
public sealed class ClipboardContent
{
    /// <summary>Copied files and folders (absolute paths).</summary>
    public string[]? Files { get; init; }
    public string? Text { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    /// <summary>An encoded image. Written as PNG; read as PNG when offered, else any format SkiaSharp decodes.</summary>
    public byte[]? Image { get; init; }
    /// <summary>
    /// Read: the copying app asked clipboard managers not to record this (password managers do).
    /// Write: ask other clipboard managers and the OS history not to record it.
    /// </summary>
    public bool IsPrivate { get; init; }

    public bool IsEmpty => Files is not { Length: > 0 } && string.IsNullOrEmpty(Text) && Image is null;
}

/// <summary>The app a clip was copied from.</summary>
public readonly record struct SourceApp(int Pid, string? Name, string? Path);

/// <summary>
/// Everything that differs between operating systems. Clipt's UI, storage, search, crypto and AI are shared;
/// each OS implements this once (Platform/Windows, Platform/Linux). All members are called on the UI thread
/// unless noted, and all events are raised on the UI thread.
/// </summary>
public interface IPlatform : IDisposable
{
    /// <summary>Short OS name for UI text, e.g. "Windows".</summary>
    string OsName { get; }

    /// <summary>Called once the Avalonia app is running; starts listening for clipboard changes.</summary>
    void Start();

    // ---------- clipboard ----------

    /// <summary>Raised (debounced by the caller) when another app changes the clipboard. Never for Clipt's own writes.</summary>
    event Action? ClipboardChanged;

    /// <summary>Reads the clipboard. May be called from a background thread on platforms that allow it.</summary>
    Task<ClipboardContent?> ReadClipboardAsync(bool includeImage);

    /// <summary>Puts content on the clipboard. Returns a token for <see cref="IsClipboardStill"/>, or 0 on failure.</summary>
    long WriteClipboard(ClipboardContent content);

    /// <summary>True if nothing has replaced the content written with <paramref name="token"/>.</summary>
    bool IsClipboardStill(long token);

    /// <summary>Empties the clipboard (only used to wipe secrets Clipt itself put there).</summary>
    void ClearClipboard();

    /// <summary>The app that owns the clipboard right now (falls back to the focused app).</summary>
    SourceApp GetClipboardSource();

    /// <summary>
    /// True for processes that re-own the clipboard to keep its content alive after the copying app quits
    /// (GNOME Shell, KDE Plasma, clipboard daemons). Their takeover is not a new copy.
    /// </summary>
    bool IsClipboardKeeper(SourceApp source);

    /// <summary>Title of the focused window, used to skip private browsing windows.</summary>
    string? GetActiveWindowTitle();

    // ---------- hotkey, focus and paste ----------

    event Action? HotkeyPressed;

    /// <summary>Registers the global launcher shortcut, replacing any previous one.</summary>
    bool RegisterHotkey(Hotkey hotkey);

    /// <summary>Whether the OS supports taking over the built-in clipboard history shortcut (Win+V).</summary>
    bool SupportsWinVTakeover { get; }
    bool WinVTakeover { get; set; }
    event Action? WinVPressed;

    /// <summary>Handle of the focused top-level window (to paste back into), or 0.</summary>
    nint GetForegroundWindow();

    /// <summary>A point (physical pixels) on the monitor where the launcher should appear.</summary>
    Avalonia.PixelPoint? GetAnchorPoint(nint targetWindow);

    /// <summary>Brings one of Clipt's windows to the front, past focus-stealing prevention.</summary>
    void ForceForeground(TopLevel window);

    /// <summary>Focuses <paramref name="target"/> and sends the paste keystroke.</summary>
    Task SendPasteAsync(nint target);

    // ---------- windows ----------

    /// <summary>Tool-window behaviour for the launcher (no taskbar/Alt+Tab entry, rounded corners…).</summary>
    void PrepareLauncherWindow(Window window);

    /// <summary>The OS accent colour tuned for a dark or light background, or null to use Clipt's default.</summary>
    Avalonia.Media.Color? GetAccentColor(bool dark);

    /// <summary>Whether <see cref="SetCaptureProtection"/> works on this OS.</summary>
    bool SupportsCaptureProtection { get; }
    void SetCaptureProtection(Window window, bool enabled);

    /// <summary>Gives memory back to the OS after the launcher hides.</summary>
    void TrimWorkingSet();

    /// <summary>Raised when the session is locked or the user switches away.</summary>
    event Action? SessionLocked;

    // ---------- shell ----------

    /// <summary>Whether "start at login" is supported, and its label.</summary>
    string StartupLabel { get; }
    void ApplyStartup(bool enabled);

    /// <summary>Opens a web link or folder with the default handler.</summary>
    void Open(string target);

    /// <summary>Shows a file in the file manager without running it.</summary>
    void Reveal(string path);

    /// <summary>Tray icon with Open / Pause / Settings / Quit.</summary>
    ITray CreateTray(App app);

    // ---------- apps ----------

    /// <summary>Apps the user may want to exclude: running apps, then installed apps. Called off the UI thread.</summary>
    IEnumerable<(string? name, string path, Services.AppOrigin origin)> EnumerateApps();

    /// <summary>A human name for an executable ("Firefox" for /usr/lib/firefox/firefox).</summary>
    string FriendlyAppName(string path, string? hint);

    /// <summary>The app's icon, if one can be found. Called off the UI thread.</summary>
    Bitmap? LoadAppIcon(string path);

    /// <summary>File picker filter for "Browse…" when choosing an app to exclude.</summary>
    (string name, string[] patterns)? ProgramFileFilter { get; }

    // ---------- security & OCR ----------

    /// <summary>Wraps secrets (the vault key, the API key) so only this user account can unwrap them.</summary>
    IKeyProtector KeyProtector { get; }

    /// <summary>A sentence describing where the key lives, for Settings.</summary>
    string KeyProtectionDescription { get; }

    /// <summary>Text recognition for images, or null when the OS has none available.</summary>
    IOcrEngine? Ocr { get; }

    /// <summary>Settings text describing the OCR engine.</summary>
    string OcrDescription { get; }
}

/// <summary>Per-OS defaults that are plain data (usable without starting the platform layer, e.g. in tests).</summary>
public static class PlatformDefaults
{
    /// <summary>Name of the "super" modifier key on this OS.</summary>
    public static string SuperKeyName => OperatingSystem.IsWindows() ? "Win" : "Super";

    public const string DefaultHotkey = "Alt+Shift+V";

    /// <summary>Tried in order when the default shortcut is taken.</summary>
    public static string[] FallbackHotkeys =>
        ["Ctrl+Shift+Space", $"{SuperKeyName}+Alt+V", "Alt+Shift+Space"];

    /// <summary>Apps excluded from recording on a fresh install (password managers), matched by executable name.</summary>
    public static List<ExcludedApp> DefaultExcludedApps() => OperatingSystem.IsWindows()
        ?
        [
            new("KeePass.exe", "KeePass"),
            new("KeePassXC.exe", "KeePassXC"),
            new("1Password.exe", "1Password"),
            new("Bitwarden.exe", "Bitwarden"),
            new("Dashlane.exe", "Dashlane"),
            new("LastPass.exe", "LastPass"),
            new("Enpass.exe", "Enpass"),
            new("RoboForm.exe", "RoboForm"),
            new("ProtonPass.exe", "Proton Pass"),
        ]
        :
        [
            new("keepassxc", "KeePassXC"),
            new("1password", "1Password"),
            new("bitwarden", "Bitwarden"),
            new("enpass", "Enpass"),
            new("proton-pass", "Proton Pass"),
            new("seahorse", "Passwords and Keys"),
            new("secrets", "Secrets"),
        ];
}

public interface ITray : IDisposable
{
    void Update(string hotkey, bool paused);
    void Notify(string title, string text);
}

/// <summary>The OS key store holds Clipt's key but can't be reached right now (as opposed to the key being gone).</summary>
public sealed class KeyStoreUnavailableException(string message) : Exception(message);

public interface IKeyProtector
{
    byte[] Protect(byte[] secret, byte[] entropy);
    /// <exception cref="System.Security.Cryptography.CryptographicException">The data can't be unwrapped by this user.</exception>
    byte[] Unprotect(byte[] blob, byte[] entropy);
}

public interface IOcrEngine
{
    /// <summary>Recognises text in an (already decrypted) image held in memory. Nothing touches the disk.</summary>
    Task<string?> RecognizeAsync(byte[] png);
}
