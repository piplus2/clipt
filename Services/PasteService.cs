using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Clipt.Models;
using Clipt.Native;

namespace Clipt.Services;

public enum PasteMode { Rich, PlainText }

public sealed class PasteService
{
    /// <summary>How long a pasted secret stays on the clipboard before Clipt wipes it.</summary>
    public static readonly TimeSpan SensitiveClipboardLifetime = TimeSpan.FromSeconds(30);

    private readonly ClipStore _store;
    private readonly ClipboardMonitor _monitor;
    private CancellationTokenSource? _clearCts;

    public PasteService(ClipStore store, ClipboardMonitor monitor)
    {
        _store = store;
        _monitor = monitor;
    }

    /// <summary>Puts the clip on the clipboard (without re-recording it) and marks it as recently used.</summary>
    public bool CopyToClipboard(ClipItem item, PasteMode mode)
    {
        var data = new DataObject();
        switch (item.Kind)
        {
            case ClipKind.Image:
                var bytes = _store.ReadImage(item.ImagePath);
                if (bytes is null) return false;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                data.SetImage(bmp);
                data.SetData("PNG", new MemoryStream(bytes));
                if (mode == PasteMode.PlainText && item.OcrText is { Length: > 0 } ocr) data.SetText(ocr);
                break;

            case ClipKind.Files:
                var files = new StringCollection();
                files.AddRange(item.Paths);
                if (mode == PasteMode.Rich) data.SetFileDropList(files);
                data.SetText(string.Join(Environment.NewLine, item.Paths));
                break;

            default:
                data.SetText(item.Text ?? "", TextDataFormat.UnicodeText);
                if (mode == PasteMode.Rich && !item.Sensitive && item.Id > 0)
                {
                    var (html, rtf) = _store.GetRichFormats(item.Id);
                    if (!string.IsNullOrEmpty(html)) data.SetData(DataFormats.Html, html);
                    if (!string.IsNullOrEmpty(rtf)) data.SetData(DataFormats.Rtf, rtf);
                }
                break;
        }

        if (item.Sensitive) MarkPrivate(data);

        bool ok = false;
        ClipboardMonitor.Retry(() => { Clipboard.SetDataObject(data, true); ok = true; });
        _monitor.MarkOwnChange();
        if (!ok) return false;

        if (item.Id > 0) _store.MarkUsed(item.Id);
        if (item.Sensitive) ScheduleClear();
        return true;
    }

    public bool CopyText(string text)
    {
        bool ok = false;
        ClipboardMonitor.Retry(() => { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true); ok = true; });
        _monitor.MarkOwnChange();
        return ok;
    }

    /// <summary>
    /// The same opt-out formats password managers use: keeps the secret out of Windows clipboard history
    /// (Win+V), cloud clipboard sync, and other clipboard managers.
    /// </summary>
    public static void MarkPrivate(DataObject data)
    {
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0, 0, 0, 0]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
    }

    /// <summary>Wipes the clipboard after a short delay, unless something else has been copied since.</summary>
    private void ScheduleClear()
    {
        _clearCts?.Cancel();
        var cts = _clearCts = new CancellationTokenSource();
        uint sequence = Win32.GetClipboardSequenceNumber();
        _ = Task.Delay(SensitiveClipboardLifetime, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (Win32.GetClipboardSequenceNumber() != sequence) return; // user copied something else
            ClipboardMonitor.Retry(Clipboard.Clear);
            _monitor.MarkOwnChange();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Returns focus to <paramref name="target"/> and sends Ctrl+V.</summary>
    public static async Task SendPasteAsync(IntPtr target)
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
}
