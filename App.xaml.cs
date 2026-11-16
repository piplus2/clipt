using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Clipt.Models;
using Clipt.Native;
using Clipt.Services;
using Clipt.UI;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Clipt;

public partial class App : Application
{
    private const string MutexName = "Clipt.SingleInstance.v1";
    private const string ShowEventName = "Clipt.Show.v1";
    private const string SettingsEventName = "Clipt.Settings.v1";
    private EventWaitHandle? _settingsEvent;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private MessageWindow? _messages;
    private HotkeyService? _hotkey;
    private WinVHook? _winV;
    private TrayIcon? _tray;
    private LauncherWindow? _launcher;
    private LauncherViewModel? _vm;
    private SettingsWindow? _settingsWindow;
    private int _capturesSincePrune;
    private System.Windows.Threading.DispatcherTimer? _expiryTimer;

    public AppSettings Settings { get; private set; } = new();
    public ClipStore Store { get; private set; } = null!;
    public ClipboardMonitor Monitor { get; private set; } = null!;
    public PasteService Paste { get; private set; } = null!;
    public AiService Ai { get; private set; } = null!;
    public OcrService Ocr { get; private set; } = null!;
    public bool IsDark { get; private set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        bool wantsSettings = e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        if (!isFirstInstance)
        {
            // Already running: ask the existing instance to show itself (or its settings).
            try { EventWaitHandle.OpenExisting(wantsSettings ? SettingsEventName : ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowLauncher), null, -1, false);
        _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsEventName);
        ThreadPool.RegisterWaitForSingleObject(_settingsEvent, (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, -1, false);

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        Directory.CreateDirectory(AppSettings.DataDir);
        Settings = AppSettings.Load();
        Store = new ClipStore(AppSettings.DataDir, OpenVault());
        ClipItem.ImageReader = Store.ReadImage;
        ClipItem.SensitiveTtl = TimeSpan.FromMinutes(Settings.SensitiveTtlMinutes);
        Monitor = new ClipboardMonitor(Store, () => Settings);
        Paste = new PasteService(Store, Monitor);
        Ai = new AiService(() => Settings);
        Ocr = new OcrService();

        _vm = new LauncherViewModel(this);
        _launcher = new LauncherWindow(this, _vm);
        _launcher.SettingsRequested += OpenSettings;
        new WindowInteropHelper(_launcher).EnsureHandle();

        _messages = new MessageWindow();
        _messages.Message += OnWindowMessage;
        Win32.AddClipboardFormatListener(_messages.Handle);
        Monitor.Captured += OnCaptured;

        _hotkey = new HotkeyService(_messages.Handle);
        _winV = new WinVHook(ShowLauncher);
        _tray = new TrayIcon(this);

        ApplySettings();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        Store.Prune(Settings.MaxItems, Settings.RetentionDays);
        ExpireSensitive();
        _expiryTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(30),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => ExpireSensitive(), Dispatcher);
        _expiryTimer.Start();
        _ = BackfillOcrAsync();

        bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            Settings.Save();
            _tray.Notify("Clipt is ready", $"Press {Settings.Hotkey} to open your clipboard history.");
        }
        if (wantsSettings) OpenSettings();
        else if (!background) ShowLauncher();
    }

    // ---------- events ----------

    private void OnWindowMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_CLIPBOARDUPDATE) Monitor.OnClipboardUpdate();
        else if (msg == Win32.WM_HOTKEY) _launcher?.Toggle();
    }

    private void OnCaptured(ClipItem item)
    {
        if (item.Kind == ClipKind.Image && item.OcrText is null && Settings.OcrImages && Ocr.Available && item.ImagePath is not null)
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
        var bytes = Store.ReadImage(path);
        var text = bytes is null ? null : await Ocr.RecognizeAsync(bytes);
        Store.SetOcrText(id, text ?? ""); // updates the shared in-memory item too
    }

    private async Task BackfillOcrAsync()
    {
        if (!Settings.OcrImages || !Ocr.Available) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
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
    /// administrator, or the folder was copied from another PC), the unreadable history is set aside and a new
    /// one is started. Data is never decrypted with anything but the current user's DPAPI key.
    /// </summary>
    private static CryptoVault OpenVault()
    {
        try
        {
            return CryptoVault.Open(AppSettings.DataDir);
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
            return CryptoVault.Open(AppSettings.DataDir);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(ApplyTheme);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Locking the PC or switching user hides Clipt and re-masks anything that was revealed.
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Dispatcher.BeginInvoke(() => _launcher?.HideLauncher());
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
        if (new WindowInteropHelper(_settingsWindow).Handle is var h && h != IntPtr.Zero) Win32.ForceForeground(h);
    }

    /// <summary>Stops recording copies from an app (by its executable). Returns the app's display name.</summary>
    public string? ExcludeApp(string? exePath, string? displayName)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var exe = Path.GetFileName(exePath);
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
        if (!Hotkey.TryParse(Settings.Hotkey, out var hk) || !_hotkey!.Register(hk))
        {
            // If the user never customised the shortcut, quietly fall back to a free default.
            var fallback = Settings.Hotkey == AppSettings.DefaultHotkey
                ? AppSettings.FallbackHotkeys.FirstOrDefault(c => Hotkey.TryParse(c, out var f) && _hotkey!.Register(f))
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

        _winV!.Enabled = Settings.UseWinV;
        ClipItem.SensitiveTtl = TimeSpan.FromMinutes(Settings.SensitiveTtlMinutes);
        _launcher?.SetCaptureProtection(Settings.HideFromScreenCapture);
        if (!AppSettings.IsPortable)
        {
            try { StartupService.Apply(Settings.StartWithWindows); } catch (Exception ex) { Log(ex); }
        }
        _tray?.Update(Settings.Hotkey, Monitor.Paused);
        ApplyTheme();
    }

    public void Quit()
    {
        _winV?.Dispose();
        _hotkey?.Unregister();
        if (_messages is not null) Win32.RemoveClipboardFormatListener(_messages.Handle);
        _messages?.Dispose();
        _tray?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---------- theme ----------

    private void ApplyTheme()
    {
        IsDark = Settings.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => IsSystemDark(),
        };

        var dicts = Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(IsDark ? "Dark" : "Light")}.xaml") };

        // Follow the Windows accent colour.
        try
        {
            var ui = new UISettings();
            var a = ui.GetColorValue(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
            var accent = Color.FromArgb(a.A, a.R, a.G, a.B);
            Resources["Accent"] = Frozen(new SolidColorBrush(accent));
            Resources["Accent.Soft"] = Frozen(new SolidColorBrush(Color.FromArgb(IsDark ? (byte)0x45 : (byte)0x2E, accent.R, accent.G, accent.B)));
            Resources["Accent.Fg"] = Frozen(new SolidColorBrush(IsDark ? Colors.Black : Colors.White));
            Resources["Fg.Highlight"] = Frozen(new SolidColorBrush(accent));
        }
        catch { /* keep defaults */ }

        _launcher?.ApplyTheme(IsDark);
        _settingsWindow?.ApplyTheme(IsDark);
    }

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
    }

    private static SolidColorBrush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

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
