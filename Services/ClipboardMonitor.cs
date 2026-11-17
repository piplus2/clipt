using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Clipt.Models;
using Clipt.Platform;

namespace Clipt.Services;

/// <summary>
/// Turns clipboard changes into <see cref="ClipItem"/>s. Honors the conventions password managers use to opt out
/// of clipboard history (Windows clipboard formats, KDE's x-kde-passwordManagerHint on Linux).
/// </summary>
public sealed partial class ClipboardMonitor
{
    private const int MaxTextLength = 2_000_000;
    private const long MaxImagePixels = 40_000_000;

    private readonly ClipStore _store;
    private readonly IPlatform _platform;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _debounce;
    private readonly int _ownPid = Environment.ProcessId;
    private bool _capturing;
    private bool _pendingAfterCapture;
    private string? _lastHash;

    public event Action<ClipItem>? Captured;

    public bool Paused { get; set; }

    public ClipboardMonitor(ClipStore store, IPlatform platform, Func<AppSettings> settings)
    {
        _store = store;
        _platform = platform;
        _settings = settings;
        // Many apps write the clipboard in several steps; wait for them to finish.
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            _ = CaptureAsync();
        });
        platform.ClipboardChanged += OnClipboardUpdate;
    }

    public void OnClipboardUpdate()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task CaptureAsync()
    {
        if (Paused) return;
        if (_capturing) { _pendingAfterCapture = true; return; }
        _capturing = true;
        try
        {
            var source = _platform.GetClipboardSource();
            if (source.Pid == _ownPid) return;

            var settings = _settings();
            if (settings.IsExcluded(source.Path)) return;
            if (settings.SkipPrivateWindows && _platform.GetActiveWindowTitle() is { } title && IsPrivateBrowserTitle(title)) return;

            ClipboardContent? content;
            try { content = await _platform.ReadClipboardAsync(settings.CaptureImages); }
            catch (Exception ex) { Debug.WriteLine($"Clipt: failed to read clipboard: {ex}"); return; }
            if (content is null || content.IsPrivate || Paused) return;

            ClipItem? item = null;
            try { item = await Task.Run(() => Read(content, settings)); }
            catch (Exception ex) { Debug.WriteLine($"Clipt: failed to read clipboard: {ex}"); }
            if (item is null) return;

            if (settings.DetectSensitive && item.Kind != ClipKind.Image && SensitiveDetector.Detect(item.Text) is { } reason)
            {
                if (settings.SensitiveHandling == "Skip") return;
                item.SetSensitive(true, reason);
                item.Html = item.Rtf = null; // keep only what's needed to paste it back
            }

            // The desktop took over the clip we just recorded because the app that copied it quit: not a new copy.
            if (item.Hash == _lastHash && _platform.IsClipboardKeeper(source)) return;

            item.SourceApp = source.Name;
            item.SourcePath = source.Path;
            _lastHash = item.Hash;
            var stored = _store.Upsert(item);
            Captured?.Invoke(stored);
        }
        finally
        {
            _capturing = false;
            if (_pendingAfterCapture)
            {
                _pendingAfterCapture = false;
                OnClipboardUpdate();
            }
        }
    }

    private ClipItem? Read(ClipboardContent data, AppSettings settings)
    {
        // 1. Files from the file manager.
        if (data.Files is { Length: > 0 } files)
        {
            var joined = string.Join("\n", files);
            return new ClipItem { Kind = ClipKind.Files, Text = joined, Hash = Sha("files:" + joined) };
        }

        // 2. Text (Office/Excel also put a bitmap on the clipboard: text wins).
        var text = data.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (text.Length > MaxTextLength) text = text[..MaxTextLength];
            return new ClipItem
            {
                Kind = Classify(text),
                Text = text,
                Html = data.Html,
                Rtf = data.Rtf,
                Hash = Sha("text:" + text),
            };
        }

        // 3. Images.
        if (settings.CaptureImages && data.Image is { } encoded && ImageCodec.Normalize(encoded, MaxImagePixels) is { } image)
        {
            var path = _store.WriteImage(image.Png, image.Hash); // encrypted on disk
            return new ClipItem
            {
                Kind = ClipKind.Image,
                ImagePath = path,
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                Hash = image.Hash,
            };
        }
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
    public static bool IsPrivateBrowserTitle(string title) =>
        title.Contains("InPrivate", StringComparison.Ordinal) ||
        title.Contains("Private Browsing", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Private Window", StringComparison.OrdinalIgnoreCase) ||
        title.EndsWith("(Private)", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Incognito", StringComparison.OrdinalIgnoreCase);

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [GeneratedRegex(@"^www\.[^\s.]+\.[^\s]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex WwwRegex();

    [GeneratedRegex(@"^(using |import |from \S+ import|#include|package |namespace |public |private |protected |static |const |let |var |function |def |class |interface |enum |struct |fn |func |return |if \(|for \(|while \(|SELECT |INSERT |UPDATE |DELETE |CREATE |<\?xml|<!DOCTYPE|<html|<div|\$ |npm |git |dotnet |pip |docker )", RegexOptions.IgnoreCase)]
    private static partial Regex CodeStartRegex();
}
