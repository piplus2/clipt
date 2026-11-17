using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Clipt.Models;
using Clipt.Platform;
using Clipt.Services;
using Clipt.UI;

namespace Clipt;

public partial class App : Application
{
    private ITray? _tray;
    private LauncherWindow? _launcher;
    private LauncherViewModel? _vm;
    private SettingsWindow? _settingsWindow;
    private int _capturesSincePrune;
    private DispatcherTimer? _expiryTimer;

    public IPlatform Platform { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = new();
    public ClipStore Store { get; private set; } = null!;
    public ClipboardMonitor Monitor { get; private set; } = null!;
    public PasteService Paste { get; private set; } = null!;
    public AiService Ai { get; private set; } = null!;
    public IOcrEngine? Ocr => Platform.Ocr;
    public bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    public new static App Current => (App)Application.Current!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var args = desktop.Args ?? [];

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log(e.Exception);
            e.Handled = true;
        };

        AppSettings.CreateDataDir();
        Platform = CreatePlatform();
        AppSettings.Protector = Platform.KeyProtector;
        AppIconCache.Platform = Platform;
        Settings = AppSettings.Load();

        CryptoVault vault;
        try { vault = OpenVault(Platform); }
        catch (KeyStoreUnavailableException ex)
        {
            // Never treat a keyring that isn't running yet as a lost key: that would set the history aside.
            Log(ex);
            Console.Error.WriteLine("Clipt: " + ex.Message);
            desktop.Shutdown(2);
            return;
        }

        Store = new ClipStore(AppSettings.DataDir, vault);
        ClipItem.ImageReader = Store.ReadImage;
        ClipItem.ThumbnailReader = Store.ReadThumbnail;
        ClipItem.ThumbnailWriter = Store.WriteThumbnail;
        ClipItem.SensitiveTtl = TimeSpan.FromMinutes(Settings.SensitiveTtlMinutes);
        Monitor = new ClipboardMonitor(Store, Platform, () => Settings);
        Paste = new PasteService(Store, Platform);
        Ai = new AiService(() => Settings);

        _vm = new LauncherViewModel(this);
        _launcher = new LauncherWindow(this, _vm);
        _launcher.SettingsRequested += OpenSettings;

        Platform.Start();
        Monitor.Captured += OnCaptured;
        Platform.HotkeyPressed += () => _launcher?.Toggle();
        Platform.WinVPressed += ShowLauncher;
        Platform.SessionLocked += () => _launcher?.HideLauncher();
        _tray = Platform.CreateTray(this);

        SingleInstance.Received += cmd => { if (cmd == "settings") OpenSettings(); else ShowLauncher(); };
        SingleInstance.Listen();

        ActualThemeVariantChanged += (_, _) => ApplyAccent();
        if (PlatformSettings is { } ps) ps.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(ApplyAccent);

        ApplySettings();

        Store.Prune(Settings.MaxItems, Settings.RetentionDays);
        ExpireSensitive();
        _expiryTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => ExpireSensitive());
        _expiryTimer.Start();
        _ = BackfillOcrAsync();

        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        bool wantsSettings = args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            Settings.Save();
            _tray.Notify("Clipt is ready", $"Press {Settings.Hotkey} to open your clipboard history.");
        }
        if (wantsSettings) OpenSettings();
        else if (!background) ShowLauncher();
    }

    private static IPlatform CreatePlatform()
    {
#if WINDOWS
        return new global::Clipt.Platform.Windows.WindowsPlatform();
#else
        return new global::Clipt.Platform.Linux.LinuxPlatform(AppSettings.DataDir);
#endif
    }

    // ---------- events ----------

    private void OnCaptured(ClipItem item)
    {
        if (item.Kind == ClipKind.Image && item.OcrText is null && Settings.OcrImages && Ocr is not null && item.ImagePath is not null)
            _ = OcrAsync(item.Id, item.ImagePath);

        if (++_capturesSincePrune >= 50)
        {
            _capturesSincePrune = 0;
            Store.Prune(Settings.MaxItems, Settings.RetentionDays);
        }

        if (_launcher is { IsVisible: true } && _vm is { IsBrowse: true })
            _vm.Reload(keepSelection: true);
    }

    private async Task OcrAsync(long id, string path)
    {
        if (Ocr is not { } ocr) return;
        var bytes = await Task.Run(() => Store.ReadImage(path));
        var text = bytes is null ? null : await ocr.RecognizeAsync(bytes);
        Store.SetOcrText(id, text ?? ""); // updates the shared in-memory item too
    }

    private async Task BackfillOcrAsync()
    {
        if (!Settings.OcrImages) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (Ocr is null) return;
        foreach (var (id, path) in Store.ImagesMissingOcr())
            await OcrAsync(id, path);
    }

    private void ExpireSensitive()
    {
        if (Store.ExpireSensitive(TimeSpan.FromMinutes(Settings.SensitiveTtlMinutes)) > 0 &&
            _launcher is { IsVisible: true } && _vm is { IsBrowse: true })
            _vm.Reload(keepSelection: true);
    }

    /// <summary>
    /// Opens the encryption key. If it exists but can't be unlocked (e.g. the Windows password was reset by an
    /// administrator, the keyring entry was deleted, or the folder was copied from another PC), the unreadable
    /// history is set aside and a new one is started. Data is never decrypted with anything but this user's key.
    /// </summary>
    private static CryptoVault OpenVault(IPlatform platform)
    {
        try
        {
            return CryptoVault.Open(AppSettings.DataDir, platform.KeyProtector);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Log(ex);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var aside = Path.Combine(AppSettings.DataDir, $"unreadable-{stamp}");
            Directory.CreateDirectory(aside);
            foreach (var name in new[] { CryptoVault.KeyFileName, ClipStore.DbFileName, ClipStore.DbFileName + "-wal", ClipStore.DbFileName + "-shm", "images" })
            {
                var src = Path.Combine(AppSettings.DataDir, name);
                try
                {
                    if (File.Exists(src)) File.Move(src, Path.Combine(aside, name));
                    else if (Directory.Exists(src)) Directory.Move(src, Path.Combine(aside, name));
                }
                catch (Exception moveEx) { Log(moveEx); }
            }
            return CryptoVault.Open(AppSettings.DataDir, platform.KeyProtector);
        }
    }

    // ---------- commands ----------

    public void ShowLauncher() => _launcher?.ShowLauncher();

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
        Platform.ForceForeground(_settingsWindow);
    }

    /// <summary>Stops recording copies from an app (by its executable). Returns the app's display name.</summary>
    public string? ExcludeApp(string? exePath, string? displayName)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var exe = AppSettings.FileName(exePath);
        var name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(exe) : displayName;
        var updated = Settings.Clone();
        updated.ExcludedApps ??= [];
        if (!updated.ExcludedApps.Any(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)))
            updated.ExcludedApps.Add(new ExcludedApp(exe, name, exePath));
        UpdateSettings(updated);
        return name;
    }

    public void TogglePause()
    {
        Monitor.Paused = !Monitor.Paused;
        _tray?.Update(Settings.Hotkey, Monitor.Paused);
    }

    public void UpdateSettings(AppSettings updated)
    {
        Settings = updated;
        Settings.Save();
        ApplySettings();
        Store.Prune(Settings.MaxItems, Settings.RetentionDays);
    }

    private void ApplySettings()
    {
        if (!Hotkey.TryParse(Settings.Hotkey, out var hk) || !Platform.RegisterHotkey(hk))
        {
            // If the user never customised the shortcut, quietly fall back to a free default.
            var fallback = Settings.Hotkey == AppSettings.DefaultHotkey
                ? AppSettings.FallbackHotkeys.FirstOrDefault(c => Hotkey.TryParse(c, out var f) && Platform.RegisterHotkey(f))
                : null;
            if (fallback is not null)
            {
                Settings.Hotkey = fallback;
                Settings.Save();
            }
            else
            {
                _tray?.Notify("Shortcut unavailable",
                    $"{Settings.Hotkey} is already used by another app. Pick another one in Clipt Settings.");
            }
        }

        if (Platform.SupportsWinVTakeover) Platform.WinVTakeover = Settings.UseWinV;
        ClipItem.SensitiveTtl = TimeSpan.FromMinutes(Settings.SensitiveTtlMinutes);
        _launcher?.SetCaptureProtection(Settings.HideFromScreenCapture);
        if (!AppSettings.IsPortable)
        {
            try { Platform.ApplyStartup(Settings.StartWithWindows); } catch (Exception ex) { Log(ex); }
        }
        _tray?.Update(Settings.Hotkey, Monitor.Paused);
        ApplyTheme();
    }

    public void Quit()
    {
        _expiryTimer?.Stop();
        _tray?.Dispose();
        _tray = null;
        Platform.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // ---------- theme ----------

    private void ApplyTheme()
    {
        RequestedThemeVariant = Settings.Theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "Light" => ThemeVariant.Light,
            _ => ThemeVariant.Default, // follow the OS
        };
        ApplyAccent();
    }

    /// <summary>Follows the OS accent colour where there is one; otherwise the theme's own accent applies.</summary>
    private void ApplyAccent()
    {
        bool dark = IsDark;
        var keys = new[] { "Accent", "Accent.Soft", "Accent.Fg", "Fg.Highlight" };
        if (Platform.GetAccentColor(dark) is { } accent)
        {
            Resources["Accent"] = new ImmutableSolidColorBrush(accent);
            Resources["Accent.Soft"] = new ImmutableSolidColorBrush(Color.FromArgb(dark ? (byte)0x45 : (byte)0x2E, accent.R, accent.G, accent.B));
            Resources["Accent.Fg"] = new ImmutableSolidColorBrush(dark ? Colors.Black : Colors.White);
            Resources["Fg.Highlight"] = new ImmutableSolidColorBrush(accent);
        }
        else
        {
            foreach (var k in keys) Resources.Remove(k);
        }
        _launcher?.ApplyTheme();
        _settingsWindow?.ApplyTheme();
    }

    public static void Log(Exception ex)
    {
        Debug.WriteLine(ex);
        try
        {
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "clipt.log"),
                $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
