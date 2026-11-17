using Avalonia.Threading;
using Clipt.Models;
using Clipt.Platform;

namespace Clipt.Services;

public enum PasteMode { Rich, PlainText }

public sealed class PasteService
{
    /// <summary>How long a pasted secret stays on the clipboard before Clipt wipes it.</summary>
    public static readonly TimeSpan SensitiveClipboardLifetime = TimeSpan.FromSeconds(30);

    private readonly ClipStore _store;
    private readonly IPlatform _platform;
    private CancellationTokenSource? _clearCts;

    public PasteService(ClipStore store, IPlatform platform)
    {
        _store = store;
        _platform = platform;
    }

    /// <summary>Puts the clip on the clipboard (without re-recording it) and marks it as recently used.</summary>
    public bool CopyToClipboard(ClipItem item, PasteMode mode)
    {
        ClipboardContent content;
        switch (item.Kind)
        {
            case ClipKind.Image:
                var bytes = _store.ReadImage(item.ImagePath);
                if (bytes is null) return false;
                content = new ClipboardContent
                {
                    Image = bytes,
                    Text = mode == PasteMode.PlainText && item.OcrText is { Length: > 0 } ocr ? ocr : null,
                    IsPrivate = item.Sensitive,
                };
                break;

            case ClipKind.Files:
                content = new ClipboardContent
                {
                    Files = mode == PasteMode.Rich ? item.Paths : null,
                    Text = string.Join(Environment.NewLine, item.Paths),
                    IsPrivate = item.Sensitive,
                };
                break;

            default:
                string? html = null, rtf = null;
                if (mode == PasteMode.Rich && !item.Sensitive && item.Id > 0)
                    (html, rtf) = _store.GetRichFormats(item.Id);
                content = new ClipboardContent { Text = item.Text ?? "", Html = html, Rtf = rtf, IsPrivate = item.Sensitive };
                break;
        }

        long token = _platform.WriteClipboard(content);
        if (token == 0) return false;

        if (item.Id > 0) _store.MarkUsed(item.Id);
        if (item.Sensitive) ScheduleClear(token);
        return true;
    }

    public bool CopyText(string text) => _platform.WriteClipboard(new ClipboardContent { Text = text }) != 0;

    /// <summary>Wipes the clipboard after a short delay, unless something else has been copied since.</summary>
    private void ScheduleClear(long token)
    {
        _clearCts?.Cancel();
        var cts = _clearCts = new CancellationTokenSource();
        _ = Task.Delay(SensitiveClipboardLifetime, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || !_platform.IsClipboardStill(token)) return; // user copied something else
                _platform.ClearClipboard();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Returns focus to <paramref name="target"/> and sends the paste keystroke.</summary>
    public Task SendPasteAsync(nint target) => _platform.SendPasteAsync(target);
}
