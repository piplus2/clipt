using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clipt.Services;

namespace Clipt.Models;

public enum ClipKind { Text = 0, Link = 1, Code = 2, Color = 3, Image = 4, Files = 5 }

public sealed partial class ClipItem : ObservableObject
{
    /// <summary>Decrypts image files; set once at startup by the app.</summary>
    public static Func<string?, byte[]?>? ImageReader { get; set; }

    /// <summary>How long sensitive clips live (for the "auto-deletes in…" hint).</summary>
    public static TimeSpan SensitiveTtl { get; set; } = TimeSpan.FromMinutes(10);

    public long Id { get; set; }
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? Html { get; set; }
    public string? Rtf { get; set; }
    public string? ImagePath { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    /// <summary>Content identity (plain digest). Only its keyed fingerprint is ever persisted.</summary>
    public string Hash { get; set; } = "";
    public string? SourceApp { get; set; }
    public string? SourcePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public int UseCount { get; set; }

    private string? _ocrText;
    public string? OcrText { get => _ocrText; set { if (Set(ref _ocrText, value)) OnPropertyChanged(nameof(HasOcrText)); } }
    public bool HasOcrText => !IsMasked && !string.IsNullOrWhiteSpace(OcrText);

    private bool _pinned;
    public bool Pinned { get => _pinned; set { if (Set(ref _pinned, value)) OnPropertyChanged(nameof(Details)); } }

    // ---------- Sensitive content ----------

    public bool Sensitive { get; private set; }
    public string? SensitiveReason { get; private set; }

    private bool _isRevealed;
    /// <summary>Temporarily shows a sensitive clip's content. Never persisted.</summary>
    public bool IsRevealed
    {
        get => _isRevealed;
        set { if (Set(ref _isRevealed, value)) RaiseDisplayChanged(); }
    }

    public bool IsMasked => Sensitive && !IsRevealed;

    public void SetSensitive(bool sensitive, string? reason)
    {
        Sensitive = sensitive;
        SensitiveReason = sensitive ? reason ?? "Marked as sensitive" : null;
        _isRevealed = false;
        RaiseDisplayChanged();
    }

    private void RaiseDisplayChanged()
    {
        foreach (var p in new[] { nameof(Sensitive), nameof(SensitiveReason), nameof(IsRevealed), nameof(IsMasked), nameof(Title),
                     nameof(Subtitle), nameof(PreviewText), nameof(Glyph), nameof(HasGlyph), nameof(ShowThumbnail),
                     nameof(ShowColorSwatch), nameof(Thumbnail), nameof(PreviewImage), nameof(HasOcrText), nameof(Details) })
            OnPropertyChanged(p);
    }

    // ---------- Display helpers ----------

    public bool IsImage => Kind == ClipKind.Image;
    public bool ShowThumbnail => Kind == ClipKind.Image && !IsMasked;
    public bool ShowColorSwatch => Kind == ClipKind.Color && !IsMasked;
    public bool HasGlyph => !ShowThumbnail && !ShowColorSwatch;

    public string Glyph => IsMasked ? "" : Kind switch
    {
        ClipKind.Link => "",
        ClipKind.Code => "",
        ClipKind.Files => "",
        ClipKind.Image => "",
        ClipKind.Color => "",
        _ => "",
    };

    public string KindLabel => Kind switch
    {
        ClipKind.Link => "Link",
        ClipKind.Code => "Code",
        ClipKind.Files => Paths.Length == 1 ? "File" : "Files",
        ClipKind.Image => "Image",
        ClipKind.Color => "Color",
        _ => "Text",
    };

    public string[] Paths => Kind == ClipKind.Files && Text is not null
        ? Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];

    private const string Dots = "••••••••••••";

    public string Title
    {
        get
        {
            if (IsMasked) return Dots;
            switch (Kind)
            {
                case ClipKind.Image:
                    return $"Image  {ImageWidth} × {ImageHeight}";
                case ClipKind.Files:
                    var p = Paths;
                    return p.Length switch
                    {
                        0 => "Files",
                        1 => Path.GetFileName(p[0].TrimEnd('\\')) is { Length: > 0 } n ? n : p[0],
                        _ => $"{p.Length} items · " + string.Join(", ", p.Take(3).Select(x => Path.GetFileName(x.TrimEnd('\\')))),
                    };
                default:
                    var t = Text ?? "";
                    if (t.Length > 400) t = t[..400];
                    return Whitespace().Replace(t, " ").Trim();
            }
        }
    }

    public string Subtitle
    {
        get
        {
            var app = string.IsNullOrEmpty(SourceApp) ? KindLabel : SourceApp;
            return Sensitive
                ? $"{SensitiveReason} · {app} · {RelativeTime(LastUsedAt)}"
                : $"{app} · {RelativeTime(LastUsedAt)}";
        }
    }

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Sensitive)
            {
                parts.Add(SensitiveReason ?? "Sensitive");
                if (Pinned) parts.Add("kept (pinned)");
                else
                {
                    var left = LastUsedAt + SensitiveTtl - DateTime.UtcNow;
                    parts.Add(left.TotalMinutes >= 1 ? $"auto-deletes in {(int)Math.Ceiling(left.TotalMinutes)} min" : "auto-deletes soon");
                }
                return string.Join("  ·  ", parts);
            }
            switch (Kind)
            {
                case ClipKind.Image: parts.Add($"{ImageWidth} × {ImageHeight} px"); break;
                case ClipKind.Files: parts.Add(Paths.Length == 1 ? "1 item" : $"{Paths.Length} items"); break;
                default:
                    var text = Text ?? "";
                    parts.Add($"{text.Length:N0} characters");
                    int words = WordRegex().Count(text);
                    if (words > 1) parts.Add($"{words:N0} words");
                    int lines = text.Count(c => c == '\n') + 1;
                    if (lines > 1) parts.Add($"{lines:N0} lines");
                    break;
            }
            parts.Add(CreatedAt.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture));
            if (UseCount > 1) parts.Add($"used {UseCount}×");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Text shown in the preview pane, capped so huge clips stay snappy.</summary>
    public string PreviewText
    {
        get
        {
            if (IsMasked) return Kind == ClipKind.Image ? "" : new string('•', Math.Clamp(Text?.Length ?? 12, 8, 32));
            var t = Kind == ClipKind.Files ? string.Join(Environment.NewLine, Paths) : Text ?? "";
            return t.Length > 50_000 ? t[..50_000] + "\n\n…" : t;
        }
    }

    /// <summary>Short prose is shown large in the preview, Spotlight-style.</summary>
    public bool IsShortText => Kind == ClipKind.Text && (Text?.Length ?? 0) <= 140 && !(Text ?? "").Contains('\n');

    public string? LinkHost =>
        Kind == ClipKind.Link && Uri.TryCreate(NormalizedUrl, UriKind.Absolute, out var u) ? u.Host : null;

    public string? NormalizedUrl
    {
        get
        {
            var t = Text?.Trim();
            if (t is null) return null;
            return t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + t : t;
        }
    }

    // ---------- Colors ----------

    public Brush? ColorBrush => Kind == ClipKind.Color && ColorParser.TryParse(Text, out var c) ? Frozen(new SolidColorBrush(c)) : null;

    public string? ColorInfo
    {
        get
        {
            if (Kind != ClipKind.Color || !ColorParser.TryParse(Text, out var c)) return null;
            var (h, s, l) = ColorParser.ToHsl(c);
            var hex = c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";
            var rgb = c.A == 255 ? $"rgb({c.R}, {c.G}, {c.B})" : $"rgba({c.R}, {c.G}, {c.B}, {c.A / 255.0:0.##})";
            return $"HEX   {hex}\nRGB   {rgb}\nHSL   hsl({h:0}, {s:0}%, {l:0}%)";
        }
    }

    // ---------- Images (decrypted in memory, never written back to disk in plaintext) ----------

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail => ShowThumbnail ? _thumbnail ??= LoadImage(96) : null;

    public ImageSource? PreviewImage => Kind == ClipKind.Image && !IsMasked ? LoadImage(Math.Min(ImageWidth, 1600)) : null;

    public ImageSource? AppIcon => AppIconCache.Get(SourcePath);

    private ImageSource? LoadImage(int decodeWidth)
    {
        var bytes = ImageReader?.Invoke(ImagePath);
        if (bytes is null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = new MemoryStream(bytes);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static string RelativeTime(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)Math.Max(1, d.TotalMinutes)} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} days ago";
        return utc.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
    }

    public void RefreshTimes()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Details));
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"\S+")] private static partial Regex WordRegex();
}
