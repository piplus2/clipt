using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipt.Models;
using Clipt.Native;

namespace Clipt.Services;

/// <summary>
/// Listens to WM_CLIPBOARDUPDATE and turns clipboard contents into <see cref="ClipItem"/>s.
/// Honors the Windows conventions password managers use to opt out of clipboard history.
/// </summary>
public sealed partial class ClipboardMonitor
{
    private const int MaxTextLength = 2_000_000;
    private const long MaxImagePixels = 40_000_000;

    private readonly ClipStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _debounce;
    private readonly int _ownPid = Environment.ProcessId;
    private uint _ownSequence;
    private uint _lastSequence;

    public event Action<ClipItem>? Captured;

    public bool Paused { get; set; }

    public ClipboardMonitor(ClipStore store, Func<AppSettings> settings)
    {
        _store = store;
        _settings = settings;
        // Many apps write the clipboard in several steps; wait for them to finish.
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            Capture();
        }, Dispatcher.CurrentDispatcher);
        _debounce.Stop();
    }

    public void OnClipboardUpdate()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Call right after Clipt itself writes the clipboard so the change isn't re-captured.</summary>
    public void MarkOwnChange() => _ownSequence = Win32.GetClipboardSequenceNumber();

    private void Capture()
    {
        if (Paused) return;
        uint seq = Win32.GetClipboardSequenceNumber();
        if (seq == _lastSequence || seq == _ownSequence) return;
        _lastSequence = seq;

        var (pid, appName, appPath) = GetSourceProcess();
        if (pid == _ownPid) return;

        var settings = _settings();
        if (settings.IsExcluded(appPath)) return;
        if (settings.SkipPrivateWindows && IsPrivateBrowserWindow(Win32.GetForegroundWindow())) return;

        IDataObject? data = Retry(Clipboard.GetDataObject);
        if (data is null) return;

        if (Has(data, "ExcludeClipboardContentFromMonitorProcessing") ||
            Has(data, "Clipboard Viewer Ignore") ||
            (Has(data, "CanIncludeInClipboardHistory") && ReadDword(data, "CanIncludeInClipboardHistory") == 0))
            return;

        ClipItem? item = null;
        try { item = Read(data, settings); }
        catch (Exception ex) { Debug.WriteLine($"Clipt: failed to read clipboard: {ex}"); }
        if (item is null) return;

        if (settings.DetectSensitive && item.Kind != ClipKind.Image && SensitiveDetector.Detect(item.Text) is { } reason)
        {
            if (settings.SensitiveHandling == "Skip") return;
            item.SetSensitive(true, reason);
            item.Html = item.Rtf = null; // keep only what's needed to paste it back
        }

        item.SourceApp = appName;
        item.SourcePath = appPath;
        var stored = _store.Upsert(item);
        Captured?.Invoke(stored);
    }

    private ClipItem? Read(IDataObject data, AppSettings settings)
    {
        // 1. Files from Explorer & co.
        if (Get(data, DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            var joined = string.Join("\n", files);
            return new ClipItem { Kind = ClipKind.Files, Text = joined, Hash = Sha("files:" + joined) };
        }

        // 2. Text (Office/Excel also put a bitmap on the clipboard: text wins).
        var text = Get(data, DataFormats.UnicodeText) as string ?? Get(data, DataFormats.Text) as string;
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (text.Length > MaxTextLength) text = text[..MaxTextLength];
            return new ClipItem
            {
                Kind = Classify(text),
                Text = text,
                Html = Get(data, DataFormats.Html) as string,
                Rtf = Get(data, DataFormats.Rtf) as string,
                Hash = Sha("text:" + text),
            };
        }

        // 3. Images.
        if (settings.CaptureImages)
        {
            var bmp = ReadImage(data);
            if (bmp is not null && (long)bmp.PixelWidth * bmp.PixelHeight <= MaxImagePixels)
                return SaveImage(bmp);
        }
        return null;
    }

    private ClipItem SaveImage(BitmapSource bmp)
    {
        var src = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = src.PixelWidth * 4;
        var pixels = new byte[stride * src.PixelHeight];
        src.CopyPixels(pixels, stride, 0);

        // Some apps hand out 32-bit DIBs with an all-zero alpha channel: treat as opaque.
        bool allTransparent = true;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { allTransparent = false; break; }
        if (allTransparent) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var hashBytes = SHA256.HashData(pixels.Concat(BitConverter.GetBytes(src.PixelWidth)).ToArray());
        var hash = "img:" + Convert.ToHexString(hashBytes);

        var final = BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var path = _store.WriteImage(ms.ToArray(), hash); // encrypted on disk

        return new ClipItem
        {
            Kind = ClipKind.Image,
            ImagePath = path,
            ImageWidth = src.PixelWidth,
            ImageHeight = src.PixelHeight,
            Hash = hash,
        };
    }

    private static BitmapSource? ReadImage(IDataObject data)
    {
        // Prefer the lossless "PNG" format (browsers, Office, Snipping Tool) – it keeps transparency.
        if (Get(data, "PNG") is MemoryStream png)
        {
            try
            {
                var dec = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return dec.Frames[0];
            }
            catch { /* fall through */ }
        }
        if (Has(data, DataFormats.Bitmap) || Has(data, DataFormats.Dib))
            return Retry(Clipboard.GetImage);
        return null;
    }

    public static ClipKind Classify(string text)
    {
        var t = text.Trim();
        if (t.Length < 2048 && !t.Contains('\n'))
        {
            if (ColorParser.TryParse(t, out _)) return ClipKind.Color;
            if (!t.Contains(' ') &&
                ((Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp" or "mailto")
                 || WwwRegex().IsMatch(t)))
                return ClipKind.Link;
        }
        return LooksLikeCode(t) ? ClipKind.Code : ClipKind.Text;
    }

    private static bool LooksLikeCode(string t)
    {
        var lines = t.Split('\n');
        if (lines.Length < 2) return CodeStartRegex().IsMatch(t) && t.Length < 400 && (t.EndsWith(';') || t.EndsWith('{') || t.Contains("=>"));
        int score = 0;
        foreach (var l in lines)
        {
            var s = l.TrimEnd();
            if (s.Length == 0) continue;
            if (s.EndsWith(';') || s.EndsWith('{') || s.EndsWith('}') || s.EndsWith(':') && l.StartsWith("    ")) score += 2;
            if (l.StartsWith("    ") || l.StartsWith('\t')) score++;
            if (CodeStartRegex().IsMatch(s.TrimStart())) score += 2;
        }
        return score >= Math.Max(3, lines.Length);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Private/incognito windows of browsers that expose it in the window title (Edge, Firefox, Brave, Opera, Vivaldi).
    /// Chrome's incognito title carries no marker, so it can't be detected this way.
    /// </summary>
    public static bool IsPrivateBrowserWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var sb = new StringBuilder(512);
        if (Win32.GetWindowText(hwnd, sb, sb.Capacity) <= 0) return false;
        return IsPrivateBrowserTitle(sb.ToString());
    }

    public static bool IsPrivateBrowserTitle(string title) =>
        title.Contains("InPrivate", StringComparison.Ordinal) ||
        title.Contains("Private Browsing", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Private Window", StringComparison.OrdinalIgnoreCase) ||
        title.EndsWith("(Private)", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Incognito", StringComparison.OrdinalIgnoreCase);

    private static (int pid, string? name, string? path) GetSourceProcess()
    {
        var hwnd = Win32.GetClipboardOwner();
        if (hwnd == IntPtr.Zero) hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (0, null, null);
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
            return ((int)pid, name, path);
        }
        catch { return ((int)pid, null, null); }
    }

    private static bool Has(IDataObject d, string format)
    {
        try { return d.GetDataPresent(format, false); } catch { return false; }
    }

    private static object? Get(IDataObject d, string format)
    {
        try { return d.GetDataPresent(format, false) ? d.GetData(format, false) : null; }
        catch { return null; }
    }

    private static int ReadDword(IDataObject d, string format)
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
    public static T? Retry<T>(Func<T?> action, int attempts = 6)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { return action(); }
            catch (COMException) { Thread.Sleep(25 * (i + 1)); }
            catch (ExternalException) { Thread.Sleep(25 * (i + 1)); }
        }
        return default;
    }

    public static void Retry(Action action, int attempts = 6) =>
        Retry<object?>(() => { action(); return null; }, attempts);

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [GeneratedRegex(@"^www\.[^\s.]+\.[^\s]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex WwwRegex();

    [GeneratedRegex(@"^(using |import |from \S+ import|#include|package |namespace |public |private |protected |static |const |let |var |function |def |class |interface |enum |struct |fn |func |return |if \(|for \(|while \(|SELECT |INSERT |UPDATE |DELETE |CREATE |<\?xml|<!DOCTYPE|<html|<div|\$ |npm |git |dotnet |pip |docker )", RegexOptions.IgnoreCase)]
    private static partial Regex CodeStartRegex();
}
