using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clipt.Platform;

namespace Clipt.Services;

/// <summary>An app whose clipboard activity is never recorded.</summary>
/// <param name="Exe">Executable file name, e.g. "chrome.exe" or "firefox" (what matching uses).</param>
/// <param name="Name">Friendly name shown to the user, e.g. "Google Chrome".</param>
/// <param name="Path">Optional full path, used only to show the app's icon.</param>
public sealed record ExcludedApp(string Exe, string Name, string? Path = null);

public sealed class AppSettings
{
    // General
    public const string DefaultHotkey = PlatformDefaults.DefaultHotkey;
    public static string[] FallbackHotkeys => PlatformDefaults.FallbackHotkeys;

    public string Hotkey { get; set; } = DefaultHotkey;
    public bool UseWinV { get; set; }
    /// <summary>Start at login (the name predates Linux support; kept so existing settings files still load).</summary>
    public bool StartWithWindows { get; set; } = true;
    public bool PasteDirectly { get; set; } = true;
    public string Theme { get; set; } = "System"; // System | Dark | Light

    // History
    public int MaxItems { get; set; } = 2000;
    public int RetentionDays { get; set; } = 30; // 0 = forever
    public bool CaptureImages { get; set; } = true;
    public bool OcrImages { get; set; } = true;

    // Privacy
    public bool DetectSensitive { get; set; } = true;
    /// <summary>"Hide": keep masked and auto-delete after <see cref="SensitiveTtlMinutes"/>. "Skip": never save.</summary>
    public string SensitiveHandling { get; set; } = "Hide";
    public int SensitiveTtlMinutes { get; set; } = 10;
    public bool SkipPrivateWindows { get; set; } = true;
    public bool HideFromScreenCapture { get; set; } = true;

    /// <summary>Apps whose copies are never recorded (matched by executable name).</summary>
    public List<ExcludedApp>? ExcludedApps { get; set; }

    /// <summary>Legacy v0.1 format (bare .exe names); migrated into <see cref="ExcludedApps"/> on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IgnoredApps { get; set; }

    public static List<ExcludedApp> DefaultExcludedApps() => PlatformDefaults.DefaultExcludedApps();

    public bool IsExcluded(string? exePath) =>
        exePath is not null && ExcludedApps is not null &&
        ExcludedApps.Any(a => string.Equals(a.Exe, FileName(exePath), StringComparison.OrdinalIgnoreCase));

    /// <summary>File name of a Windows or Unix path, whatever OS Clipt runs on.</summary>
    internal static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    // AI (opt-in)
    public bool AiEnabled { get; set; }
    public string AiProvider { get; set; } = "Anthropic"; // Anthropic | Ollama
    public string AnthropicModel { get; set; } = "claude-opus-5-5";
    public string? AnthropicKeyProtected { get; set; }
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.2";
    public string TranslateTo { get; set; } = "English";

    public bool FirstRunDone { get; set; }

    [JsonIgnore]
    public string? AnthropicKey
    {
        get
        {
            if (string.IsNullOrEmpty(AnthropicKeyProtected)) return null;
            try
            {
                if (Protector is null) return null;
                var bytes = Protector.Unprotect(Convert.FromBase64String(AnthropicKeyProtected), Entropy);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            AnthropicKeyProtected = string.IsNullOrWhiteSpace(value) || Protector is null
                ? null
                : Convert.ToBase64String(Protector.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy));
        }
    }

    private static readonly byte[] Entropy = "Clipt.v1"u8.ToArray();

    /// <summary>Wraps the API key for this user account (DPAPI / desktop keyring). Set once at startup.</summary>
    public static IKeyProtector? Protector { get; set; }

    // ---------- persistence ----------

    /// <summary>Set CLIPT_DATA_DIR to run an isolated (portable/dev) instance.</summary>
    public static bool IsPortable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLIPT_DATA_DIR"));

    public static string DataDir { get; } = IsPortable
        ? Environment.GetEnvironmentVariable("CLIPT_DATA_DIR")!
        : OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipt")
            // ~/.local/share/clipt (XDG_DATA_HOME)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipt");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch { s = new AppSettings(); /* corrupt settings: fall back to defaults */ }
        s.Normalize();
        return s;
    }

    internal void Normalize()
    {
        if (ExcludedApps is null)
        {
            ExcludedApps = IgnoredApps is { Count: > 0 }
                ? IgnoredApps.Select(e => DefaultExcludedApps().FirstOrDefault(d => d.Exe.Equals(e, StringComparison.OrdinalIgnoreCase))
                                          ?? new ExcludedApp(e, Path.GetFileNameWithoutExtension(e))).ToList()
                : DefaultExcludedApps();
        }
        IgnoredApps = null;
        if (SensitiveHandling is not ("Hide" or "Skip")) SensitiveHandling = "Hide";
        SensitiveTtlMinutes = Math.Clamp(SensitiveTtlMinutes, 1, 24 * 60);
    }

    public void Save()
    {
        CreateDataDir();
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Creates the data folder; on Linux only the user can enter it (0700).</summary>
    public static void CreateDataDir()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DataDir);
        else Directory.CreateDirectory(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;
}
