using System.Text;
using Avalonia.Input;
using Clipt.Platform.Linux;
using Clipt.Services;
using Xunit;

namespace Clipt.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Shift+V", KeyModifiers.Alt | KeyModifiers.Shift, Key.V)]
    [InlineData("Ctrl+Shift+Space", KeyModifiers.Control | KeyModifiers.Shift, Key.Space)]
    [InlineData("Win+Alt+V", KeyModifiers.Meta | KeyModifiers.Alt, Key.V)]
    [InlineData("Super+Alt+V", KeyModifiers.Meta | KeyModifiers.Alt, Key.V)]
    [InlineData("ctrl+,", KeyModifiers.Control, Key.OemComma)]
    [InlineData("Ctrl+Alt+1", KeyModifiers.Control | KeyModifiers.Alt, Key.D1)]
    [InlineData("Ctrl+F12", KeyModifiers.Control, Key.F12)]
    public void Parses_shortcuts_from_either_OS(string text, KeyModifiers mods, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(new Hotkey(mods, key), hk);
        // Round-trips through its own display form.
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again));
        Assert.Equal(hk, again);
    }

    [Theory]
    [InlineData("V")]          // no modifier
    [InlineData("Ctrl+")]      // no key
    [InlineData("Ctrl+Nope")]  // unknown key
    [InlineData("Ctrl+42")]    // numbers aren't key names
    [InlineData("")]
    public void Rejects_invalid_shortcuts(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Every_key_a_shortcut_can_use_maps_to_an_X_keysym()
    {
        foreach (var k in new[] { Key.A, Key.Z, Key.D0, Key.D9, Key.F1, Key.F24, Key.Space, Key.OemComma, Key.Oem3, Key.Insert })
            Assert.NotNull(LinuxPlatform.Keysym(k));
        Assert.Equal((uint)'v', LinuxPlatform.Keysym(Key.V));
        Assert.Equal((uint)'1', LinuxPlatform.Keysym(Key.D1));
        Assert.Equal(0xffbeu, LinuxPlatform.Keysym(Key.F1));
    }
}

public class LinuxClipboardFormatTests
{
    [Fact]
    public void File_uri_lists_become_local_paths()
    {
        var paths = X11Session.ParseFileUris("file:///home/me/My%20Report.pdf\r\nfile:///tmp/a.txt\r\n")!;
        Assert.Equal(["/home/me/My Report.pdf", "/tmp/a.txt"], paths);
    }

    [Fact]
    public void Gnome_copied_files_header_is_ignored()
    {
        var paths = X11Session.ParseFileUris("copy\nfile:///home/me/notes.md")!;
        Assert.Equal(["/home/me/notes.md"], paths);
    }

    [Fact]
    public void Web_links_in_a_uri_list_stay_text()
    {
        Assert.Null(X11Session.ParseFileUris("https://example.com/\r\n"));
        Assert.Null(X11Session.ParseFileUris("file:///tmp/a\r\nhttps://example.com/"));
        Assert.Null(X11Session.ParseFileUris("# comment only\n"));
    }

    [Fact]
    public void Html_is_decoded_from_utf8_and_utf16()
    {
        const string html = "<b>Grüße</b>";
        Assert.Equal(html, X11Session.DecodeHtml(Encoding.UTF8.GetBytes(html)));
        Assert.Equal(html, X11Session.DecodeHtml([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(html)])); // Firefox: UTF-16 with BOM
        Assert.Equal(html, X11Session.DecodeHtml(Encoding.Unicode.GetBytes(html)));                 // UTF-16 without BOM
        Assert.Equal(html, X11Session.DecodeHtml(Encoding.UTF8.GetBytes(html + "\0")));
    }
}

public class ImageCodecTests
{
    private static byte[] Png(int w, int h, byte alpha)
    {
        using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
        bmp.Erase(new SkiaSharp.SKColor(10, 120, 200, alpha));
        using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Same_pixels_give_the_same_identity_whatever_the_encoding()
    {
        var png = Png(20, 10, 255);
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        using var jpegLossless = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 100); // lossless WebP
        var a = ImageCodec.Normalize(png, 1_000_000)!;
        var b = ImageCodec.Normalize(jpegLossless.ToArray(), 1_000_000)!;
        Assert.Equal((20, 10), (a.Width, a.Height));
        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void Fully_transparent_images_are_made_opaque()
    {
        var n = ImageCodec.Normalize(Png(4, 4, 0), 1_000_000)!;
        using var decoded = SkiaSharp.SKBitmap.Decode(n.Png);
        Assert.Equal(255, decoded.GetPixel(1, 1).Alpha);
    }

    [Fact]
    public void Huge_images_and_garbage_are_rejected()
    {
        Assert.Null(ImageCodec.Normalize(Png(100, 100, 255), maxPixels: 5_000));
        Assert.Null(ImageCodec.Normalize("not an image"u8.ToArray(), 1_000_000));
    }

    [Fact]
    public void Thumbnails_are_scaled_down_never_up()
    {
        using var thumb = SkiaSharp.SKBitmap.Decode(ImageCodec.Downscale(Png(400, 200, 255), 96)!);
        Assert.Equal((96, 48), (thumb.Width, thumb.Height));
        var small = Png(50, 50, 255);
        Assert.Same(small, ImageCodec.Downscale(small, 96));
    }
}
