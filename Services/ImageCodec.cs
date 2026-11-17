using System.Security.Cryptography;
using SkiaSharp;

namespace Clipt.Services;

/// <summary>Image decoding and encoding for clips (SkiaSharp, which Avalonia already ships).</summary>
public static unsafe class ImageCodec
{
    public sealed record Normalized(byte[] Png, int Width, int Height, string Hash);

    /// <summary>
    /// Decodes any supported image, converts it to 32-bit BGRA, fixes all-transparent DIBs, and re-encodes it as PNG.
    /// The hash is over the pixels, so the same image copied from different apps (or encoders) is one clip.
    /// </summary>
    public static Normalized? Normalize(byte[] encoded, long maxPixels)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(encoded));
        if (codec is null) return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > maxPixels) return null;

        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        var pixels = new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount);
        // Some apps hand out 32-bit DIBs with an all-zero alpha channel: treat them as opaque.
        bool allTransparent = true;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { allTransparent = false; break; }
        if (allTransparent) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var hashInput = new byte[pixels.Length + sizeof(int)];
        pixels.CopyTo(hashInput);
        BitConverter.GetBytes(info.Width).CopyTo(hashInput, pixels.Length);
        var hash = "img:" + Convert.ToHexString(SHA256.HashData(hashInput));

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data is null ? null : new Normalized(data.ToArray(), info.Width, info.Height, hash);
    }

    /// <summary>A PNG scaled down to <paramref name="width"/> pixels wide (never up).</summary>
    public static byte[]? Downscale(byte[] encoded, int width)
    {
        using var source = SKBitmap.Decode(encoded);
        if (source is null) return null;
        if (source.Width <= width) return encoded;
        int height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
        using var scaled = source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var data = scaled?.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
