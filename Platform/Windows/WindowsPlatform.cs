using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clipt.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Clipt.Platform.Windows;

/// <summary>Windows 10/11 implementation of <see cref="IPlatform"/> (Win32, WinForms clipboard and tray, WinRT OCR, DPAPI).</summary>
internal sealed class WindowsPlatform : IPlatform
{
    private const int HotkeyId = 0xC11;
    private MessageWindow? _messages;
    private WinVHook? _winV;
    private bool _hotkeyRegistered;
    private uint _ownSequence;
    private readonly Lazy<IOcrEngine?> _ocr = new(WindowsOcr.TryCreate);

    public string OsName => "Windows";
    public void Start()
    {
        _messages = new MessageWindow();
        _messages.Message += OnMessage;
        Win32.AddClipboardFormatListener(_messages.Handle);
        _winV = new WinVHook(() => WinVPressed?.Invoke());
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnMessage(int msg)
    {
        if (msg == Win32.WM_HOTKEY) HotkeyPressed?.Invoke();
        else if (msg == Win32.WM_CLIPBOARDUPDATE && Win32.GetClipboardSequenceNumber() != _ownSequence) ClipboardChanged?.Invoke();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Locking the PC or switching user hides Clipt and re-masks anything that was revealed.
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Dispatcher.UIThread.Post(() => SessionLocked?.Invoke());
    }

    // ---------- clipboard ----------

    public event Action? ClipboardChanged;

    /// <summary>The Win32 clipboard needs the (STA) UI thread, so this reads synchronously.</summary>
    public Task<ClipboardContent?> ReadClipboardAsync(bool includeImage)
    {
        var data = Retry(WinForms.Clipboard.GetDataObject);
        if (data is null) return Task.FromResult<ClipboardContent?>(null);

        // The conventions password managers use to opt out of clipboard history.
        bool isPrivate = Has(data, "ExcludeClipboardContentFromMonitorProcessing") ||
                         Has(data, "Clipboard Viewer Ignore") ||
                         (Has(data, "CanIncludeInClipboardHistory") && ReadDword(data, "CanIncludeInClipboardHistory") == 0);

        var files = Get(data, WinForms.DataFormats.FileDrop) as string[];
        var text = Get(data, WinForms.DataFormats.UnicodeText) as string ?? Get(data, WinForms.DataFormats.Text) as string;
        string? html = null, rtf = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            html = Get(data, WinForms.DataFormats.Html) as string;
            rtf = Get(data, WinForms.DataFormats.Rtf) as string;
        }

        byte[]? image = null;
        if (includeImage && files is not { Length: > 0 } && string.IsNullOrWhiteSpace(text))
        {
            // Prefer the lossless "PNG" format (browsers, Office, Snipping Tool): it keeps transparency.
            if (Get(data, "PNG") is MemoryStream png) image = png.ToArray();
            else if (Has(data, WinForms.DataFormats.Bitmap) || Has(data, WinForms.DataFormats.Dib))
            {
                using var bmp = Retry(WinForms.Clipboard.GetImage);
                if (bmp is not null)
                {
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    image = ms.ToArray();
                }
            }
        }

        return Task.FromResult<ClipboardContent?>(new ClipboardContent
        {
            Files = files, Text = text, Html = html, Rtf = rtf, Image = image, IsPrivate = isPrivate,
        });
    }

    public long WriteClipboard(ClipboardContent content)
    {
        var data = new WinForms.DataObject();
        if (content.Image is { } png)
        {
            using (var ms = new MemoryStream(png))
            using (var bmp = new System.Drawing.Bitmap(ms))
                data.SetImage(new System.Drawing.Bitmap(bmp));
            data.SetData("PNG", new MemoryStream(png));
        }
        if (content.Files is { Length: > 0 } files)
        {
            var list = new StringCollection();
            list.AddRange(files);
            data.SetFileDropList(list);
        }
        if (content.Text is { } text) data.SetText(text, WinForms.TextDataFormat.UnicodeText);
        if (!string.IsNullOrEmpty(content.Html)) data.SetData(WinForms.DataFormats.Html, content.Html);
        if (!string.IsNullOrEmpty(content.Rtf)) data.SetData(WinForms.DataFormats.Rtf, content.Rtf);
        if (content.IsPrivate)
        {
            // Keeps the secret out of Windows clipboard history (Win+V), cloud clipboard sync and other managers.
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0, 0, 0, 0]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        }

        bool ok = false;
        Retry(() => { WinForms.Clipboard.SetDataObject(data, true); ok = true; });
        _ownSequence = Win32.GetClipboardSequenceNumber();
        return ok ? Math.Max(1, _ownSequence) : 0;
    }

    public bool IsClipboardStill(long token) => token != 0 && Win32.GetClipboardSequenceNumber() == token;

    public void ClearClipboard()
    {
        Retry(WinForms.Clipboard.Clear);
        _ownSequence = Win32.GetClipboardSequenceNumber();
    }

    public SourceApp GetClipboardSource()
    {
        var hwnd = Win32.GetClipboardOwner();
        if (hwnd == IntPtr.Zero) hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return default;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* elevated or protected process */ }
            string? name = null;
            if (path is not null)
            {
                try { name = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
            }
            if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
            return new SourceApp((int)pid, name, path);
        }
        catch { return new SourceApp((int)pid, null, null); }
    }

    public bool IsClipboardKeeper(SourceApp source) => false;

    public string? GetActiveWindowTitle()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var sb = new StringBuilder(512);
        return Win32.GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    private static bool Has(WinForms.IDataObject d, string format)
    {
        try { return d.GetDataPresent(format, false); } catch { return false; }
    }

    private static object? Get(WinForms.IDataObject d, string format)
    {
        try { return d.GetDataPresent(format, false) ? d.GetData(format, false) : null; }
        catch { return null; }
    }

    private static int ReadDword(WinForms.IDataObject d, string format)
    {
        if (Get(d, format) is MemoryStream ms && ms.Length >= 4)
        {
            var b = new byte[4];
            ms.Position = 0;
            ms.ReadExactly(b);
            return BitConverter.ToInt32(b);
        }
        return -1;
    }

    /// <summary>The clipboard is a shared, lockable resource; another process may briefly hold it.</summary>
    private static T? Retry<T>(Func<T?> action, int attempts = 6)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { return action(); }
            catch (ExternalException) { Thread.Sleep(25 * (i + 1)); }
        }
        return default;
    }

    private static void Retry(Action action, int attempts = 6) => Retry<object?>(() => { action(); return null; }, attempts);

    // ---------- hotkey, focus and paste ----------

    public event Action? HotkeyPressed;

    public bool RegisterHotkey(Hotkey hotkey)
    {
        if (_messages is null) return false;
        if (_hotkeyRegistered) Win32.UnregisterHotKey(_messages.Handle, HotkeyId);
        _hotkeyRegistered = false;
        uint? vk = VirtualKey(hotkey.Key);
        if (vk is null) return false;
        uint mods = Win32.MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= Win32.MOD_ALT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Control)) mods |= Win32.MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= Win32.MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= Win32.MOD_WIN;
        _hotkeyRegistered = Win32.RegisterHotKey(_messages.Handle, HotkeyId, mods, vk.Value);
        return _hotkeyRegistered;
    }

    public bool SupportsWinVTakeover => true;

    public bool WinVTakeover
    {
        get => _winV?.Enabled == true;
        set { if (_winV is not null) _winV.Enabled = value; }
    }

    public event Action? WinVPressed;

    public nint GetForegroundWindow() => Win32.GetForegroundWindow();

    public PixelPoint? GetAnchorPoint(nint targetWindow)
    {
        if (targetWindow != 0 && Win32.GetWindowRect(targetWindow, out var r) && r.Width > 0)
            return new PixelPoint(r.Left + r.Width / 2, r.Top + r.Height / 2);
        return Win32.GetCursorPos(out var pt) ? new PixelPoint(pt.X, pt.Y) : null;
    }

    public void ForceForeground(TopLevel window)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } h && h != 0) Win32.ForceForeground(h);
    }

    /// <summary>Returns focus to <paramref name="target"/> and sends Ctrl+V.</summary>
    public async Task SendPasteAsync(nint target)
    {
        if (target == IntPtr.Zero || !Win32.IsWindow(target)) return;

        Win32.ForceForeground(target);
        for (int i = 0; i < 25 && Win32.GetForegroundWindow() != target; i++)
            await Task.Delay(15);

        // Wait for the user to release modifiers (e.g. Shift from Shift+Enter, Alt from Alt+1),
        // otherwise the target would receive Ctrl+Shift+V / Ctrl+Alt+V.
        for (int i = 0; i < 60 && (Win32.IsKeyDown(Win32.VK_SHIFT) || Win32.IsKeyDown(Win32.VK_MENU) ||
                                   Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN)); i++)
            await Task.Delay(15);

        var inputs = new List<Win32.INPUT>();
        if (Win32.IsKeyDown(Win32.VK_SHIFT)) inputs.Add(Win32.Key(Win32.VK_SHIFT, true));
        inputs.Add(Win32.Key(Win32.VK_CONTROL, false));
        inputs.Add(Win32.Key(Win32.VK_V, false));
        inputs.Add(Win32.Key(Win32.VK_V, true));
        inputs.Add(Win32.Key(Win32.VK_CONTROL, true));
        await Task.Delay(20);
        Win32.SendKeys([.. inputs]);
    }

    // ---------- windows ----------

    public void PrepareLauncherWindow(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        // Hide from Alt+Tab and the taskbar.
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex = (ex | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        // Windows 11 rounded corners for the chrome-less launcher.
        int corner = 2; // DWMWCP_ROUND
        Win32.DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corner, sizeof(int));
    }

    /// <summary>The Windows accent colour, in the shade Windows itself uses on dark or light surfaces.</summary>
    public Avalonia.Media.Color? GetAccentColor(bool dark)
    {
        try
        {
            var a = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(
                dark ? global::Windows.UI.ViewManagement.UIColorType.AccentLight2 : global::Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Avalonia.Media.Color.FromArgb(a.A, a.R, a.G, a.B);
        }
        catch { return null; }
    }

    public bool SupportsCaptureProtection => true;

    /// <summary>Excludes a window from screenshots, screen recording and screen sharing (Teams, Zoom, OBS…).</summary>
    public void SetCaptureProtection(Window window, bool enabled)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != 0)
            Win32.SetWindowDisplayAffinity(hwnd, enabled ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
    }

    public void TrimWorkingSet() =>
        Win32.SetProcessWorkingSetSize(Win32.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));

    public event Action? SessionLocked;

    // ---------- shell ----------

    public string StartupLabel => "Start with Windows";

    /// <summary>Per-user "start with Windows" via the HKCU Run key (no admin rights needed).</summary>
    public void ApplyStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;
        var exe = Environment.ProcessPath;
        if (enabled && exe is not null) key.SetValue("Clipt", $"\"{exe}\" --background");
        else if (key.GetValue("Clipt") is not null) key.DeleteValue("Clipt", false);
    }

    public void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    /// <summary>Files are revealed in Explorer, never launched: a copied .exe/.bat must not run from here.</summary>
    public void Reveal(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    public ITray CreateTray(App app) => new WindowsTray(app);

    // ---------- apps ----------

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
        "TextInputHost.exe", "SystemSettings.exe", "LockApp.exe", "Clipt.exe", "conhost.exe", "dwm.exe",
        "unins000.exe", "uninstall.exe", "setup.exe", "update.exe", "msiexec.exe", "rundll32.exe",
        "control.exe", "mmc.exe", "explorer.exe", "msconfig.exe", "regedit.exe", "MdSched.exe", "dfrgui.exe",
        "cleanmgr.exe", "RecoveryDrive.exe", "iscsicpl.exe", "odbcad32.exe", "msinfo32.exe", "perfmon.exe",
        "resmon.exe", "WFS.exe", "psr.exe", "narrator.exe", "osk.exe", "magnify.exe",
    };

    public IEnumerable<(string? name, string path, AppOrigin origin)> EnumerateApps()
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
            if (path is not null && !string.IsNullOrEmpty(title) && IsInteresting(path)) yield return (null, path, AppOrigin.Running);
        }
        foreach (var (name, path) in StartMenuApps())
            if (IsInteresting(path)) yield return (name, path, AppOrigin.Installed);
    }

    private static bool IsInteresting(string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var exe = Path.GetFileName(path);
        return !Noise.Contains(exe) && !exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
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
                yield return (Path.GetFileNameWithoutExtension(lnk), target);
            }
        }
        Marshal.FinalReleaseComObject(shell);
    }

    public string FriendlyAppName(string path, string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && !hint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return hint.Trim();
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    public Bitmap? LoadAppIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch { return null; }
    }

    public (string name, string[] patterns)? ProgramFileFilter => ("Programs", ["*.exe"]);

    // ---------- security & OCR ----------

    public IKeyProtector KeyProtector { get; } = new DpapiKeyProtector();

    public string KeyProtectionDescription =>
        "Your history is encrypted (AES-256) with a key only your Windows account can unlock.";

    public IOcrEngine? Ocr => _ocr.Value;

    public string OcrDescription => "Uses the OCR built into Windows. Runs on your PC, nothing is uploaded.";

    public void Dispose()
    {
        _winV?.Dispose();
        if (_messages is not null)
        {
            if (_hotkeyRegistered) Win32.UnregisterHotKey(_messages.Handle, HotkeyId);
            Win32.RemoveClipboardFormatListener(_messages.Handle);
            _messages.DestroyHandle();
        }
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    // ---------- keys ----------

    /// <summary>Win32 virtual-key code for an Avalonia key (the subset usable in a global shortcut).</summary>
    internal static uint? VirtualKey(Key k) => k switch
    {
        >= Key.A and <= Key.Z => 0x41 + (uint)(k - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (uint)(k - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (uint)(k - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (uint)(k - Key.F1),
        Key.Space => 0x20,
        Key.OemComma => 0xBC,
        Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF,
        Key.OemSemicolon => 0xBA,
        Key.OemQuotes => 0xDE,
        Key.OemOpenBrackets => 0xDB,
        Key.OemCloseBrackets => 0xDD,
        Key.OemMinus => 0xBD,
        Key.OemPlus => 0xBB,
        Key.Oem3 => 0xC0,
        Key.Oem5 => 0xDC,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Enter => 0x0D,
        Key.Tab => 0x09,
        Key.Escape => 0x1B,
        Key.Back => 0x08,
        Key.Pause => 0x13,
        Key.PrintScreen => 0x2C,
        _ => null,
    };

    /// <summary>Invisible message-only window that receives clipboard and hotkey notifications.</summary>
    private sealed class MessageWindow : WinForms.NativeWindow
    {
        public event Action<int>? Message;

        public MessageWindow() => CreateHandle(new WinForms.CreateParams { Caption = "CliptMessageWindow", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });

        protected override void WndProc(ref WinForms.Message m)
        {
            if (m.Msg is Win32.WM_CLIPBOARDUPDATE or Win32.WM_HOTKEY) Message?.Invoke(m.Msg);
            base.WndProc(ref m);
        }
    }
}

/// <summary>Windows DPAPI (CurrentUser scope): only the same Windows account on the same PC can unwrap.</summary>
internal sealed class DpapiKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] secret, byte[] entropy) => ProtectedData.Protect(secret, entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] blob, byte[] entropy) => ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser);
}
