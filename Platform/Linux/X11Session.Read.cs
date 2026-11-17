using System.Text;
using static Clipt.Platform.Linux.Xlib;

namespace Clipt.Platform.Linux;

/// <summary>Reading the clipboard (as a requestor). Async, so kept out of the unsafe part of the class.</summary>
internal sealed partial class X11Session
{
    /// <summary>Asks the clipboard owner for <paramref name="target"/> and waits for the data.</summary>
    private async Task<(byte[]? data, nint type)> ReadTargetAsync(nint target)
    {
        await _readGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<(byte[]?, nint)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Run(() =>
            {
                _read = new PendingRead { Tcs = tcs, Deadline = Environment.TickCount64 + ReadTimeoutMs };
                XDeleteProperty(_dpy, _win, _property);
                XConvertSelection(_dpy, _clipboard, target, _property, _win, CurrentTime);
                return true;
            }).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { _readGate.Release(); }
    }

    /// <summary>Reads what Clipt cares about from the current clipboard owner.</summary>
    public async Task<ClipboardContent?> ReadClipboardAsync(bool includeImage)
    {
        var (targetsData, _) = await ReadTargetAsync(_targets).ConfigureAwait(false);
        if (targetsData is null) return null;
        var targets = Longs(targetsData).ToHashSet();
        var names = await Run(() => targets.ToDictionary(t => t, t => AtomName(_dpy, t) ?? "")).ConfigureAwait(false);

        async Task<byte[]?> Get(nint atom) => targets.Contains(atom) ? (await ReadTargetAsync(atom).ConfigureAwait(false)).data : null;

        bool isPrivate = false;
        if (await Get(_kdePasswordHint) is { } hint)
            isPrivate = Encoding.UTF8.GetString(hint).Trim('\0', ' ', '\n') == "secret";

        string[]? files = null;
        if ((await Get(_gnomeFiles) ?? await Get(_uriList)) is { } uriBytes)
            files = ParseFileUris(Encoding.UTF8.GetString(uriBytes));

        string? text = null;
        foreach (var t in new[] { _utf8, _textPlainUtf8, _textPlain, XA_STRING, _text })
        {
            if (await Get(t) is not { } bytes) continue;
            text = (t == XA_STRING ? Encoding.Latin1.GetString(bytes) : Encoding.UTF8.GetString(bytes)).TrimEnd('\0');
            break;
        }

        string? html = null, rtf = null;
        if (!string.IsNullOrEmpty(text))
        {
            if (await Get(_html) is { } h) html = DecodeHtml(h);
            var rtfAtom = names.FirstOrDefault(kv => kv.Value is "text/rtf" or "text/richtext" or "application/rtf").Key;
            if (rtfAtom != 0 && await Get(rtfAtom) is { } r) rtf = Encoding.UTF8.GetString(r).TrimEnd('\0');
        }

        byte[]? image = null;
        if (includeImage && files is null && string.IsNullOrWhiteSpace(text))
        {
            var imageAtom = targets.Contains(_png) ? _png
                : names.FirstOrDefault(kv => kv.Value is "image/jpeg" or "image/bmp" or "image/webp" or "image/gif" or "image/tiff").Key;
            if (imageAtom != 0) image = await Get(imageAtom);
        }

        return new ClipboardContent { Files = files, Text = text, Html = html, Rtf = rtf, Image = image, IsPrivate = isPrivate };
    }
}
