using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Clipt.UI;

/// <summary>Draws the Clipt logo in code so the app has no binary assets.</summary>
public static class IconFactory
{
    private static Bitmap? _bitmap;
    private static BitmapSource? _image;

    public static Bitmap Bitmap => _bitmap ??= Draw(128);

    public static Icon CreateIcon(int size = 32)
    {
        using var bmp = Draw(size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static BitmapSource AppImage
    {
        get
        {
            if (_image is not null) return _image;
            var hbmp = Bitmap.GetHbitmap(System.Drawing.Color.Transparent);
            _image = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            _image.Freeze();
            return _image;
        }
    }

    private static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = size / 64f;

        // Rounded-square tile with a cool gradient.
        var tile = new RectangleF(2 * s, 2 * s, 60 * s, 60 * s);
        using (var path = Rounded(tile, 15 * s))
        using (var brush = new LinearGradientBrush(tile, System.Drawing.Color.FromArgb(255, 72, 132, 255), System.Drawing.Color.FromArgb(255, 150, 92, 255), 50f))
            g.FillPath(brush, path);

        // Clipboard body.
        using (var pen = new Pen(System.Drawing.Color.White, 4.2f * s) { LineJoin = LineJoin.Round })
        using (var body = Rounded(new RectangleF(18 * s, 16 * s, 28 * s, 34 * s), 6 * s))
            g.DrawPath(pen, body);

        // Clip.
        using (var clip = Rounded(new RectangleF(25 * s, 11 * s, 14 * s, 9 * s), 3.5f * s))
        using (var white = new SolidBrush(System.Drawing.Color.White))
            g.FillPath(white, clip);

        // Text lines.
        using (var line = new Pen(System.Drawing.Color.FromArgb(230, 255, 255, 255), 3.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(line, 25 * s, 30 * s, 39 * s, 30 * s);
            g.DrawLine(line, 25 * s, 38 * s, 34 * s, 38 * s);
        }
        return bmp;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
