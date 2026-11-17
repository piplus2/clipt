using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Clipt.UI;

/// <summary>Draws the Clipt logo in code so the app has no binary assets.</summary>
public static class IconFactory
{
    private static Bitmap? _image;

    /// <summary>The logo at 128 px for windows and the Linux tray.</summary>
    public static Bitmap AppImage => _image ??= new Bitmap(new MemoryStream(Png(128)));

    public static Avalonia.Controls.WindowIcon WindowIcon => new(AppImage);

    /// <summary>The logo as a PNG of <paramref name="size"/> × <paramref name="size"/> pixels.</summary>
    public static byte[] Png(int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        float s = size / 64f;

        // Rounded-square tile with a cool gradient (top-left blue to bottom-right violet).
        var tile = new SKRect(2 * s, 2 * s, 62 * s, 62 * s);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(tile.Left, tile.Top), new SKPoint(tile.Right, tile.Bottom),
                   [new SKColor(72, 132, 255), new SKColor(150, 92, 255)], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
            c.DrawRoundRect(tile, 15 * s, 15 * s, paint);

        // Clipboard body.
        using (var pen = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4.2f * s, StrokeJoin = SKStrokeJoin.Round })
            c.DrawRoundRect(new SKRect(18 * s, 16 * s, 46 * s, 50 * s), 6 * s, 6 * s, pen);

        // Clip.
        using (var white = new SKPaint { Color = SKColors.White, IsAntialias = true })
            c.DrawRoundRect(new SKRect(25 * s, 11 * s, 39 * s, 20 * s), 3.5f * s, 3.5f * s, white);

        // Text lines.
        using (var line = new SKPaint { Color = new SKColor(255, 255, 255, 230), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f * s, StrokeCap = SKStrokeCap.Round })
        {
            c.DrawLine(25 * s, 30 * s, 39 * s, 30 * s, line);
            c.DrawLine(25 * s, 38 * s, 34 * s, 38 * s, line);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
