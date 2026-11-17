using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Clipt.Platform.Windows;

/// <summary>Uses the OCR engine built into Windows (offline, no extra download) to make images searchable.</summary>
internal sealed class WindowsOcr : IOcrEngine
{
    private readonly OcrEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WindowsOcr(OcrEngine engine) => _engine = engine;

    public static WindowsOcr? TryCreate() =>
        OcrEngine.TryCreateFromUserProfileLanguages() is { } engine ? new WindowsOcr(engine) : null;

    /// <summary>Recognises text in an (already decrypted) image held in memory. Nothing touches the disk.</summary>
    public async Task<string?> RecognizeAsync(byte[] imageBytes)
    {
        await _gate.WaitAsync();
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            uint max = OcrEngine.MaxImageDimension;
            SoftwareBitmap bitmap;
            if (decoder.PixelWidth > max || decoder.PixelHeight > max)
            {
                double scale = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)(decoder.PixelWidth * scale),
                    ScaledHeight = (uint)(decoder.PixelHeight * scale),
                    InterpolationMode = BitmapInterpolationMode.Fant,
                };
                bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            }
            else
            {
                bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            }

            using (bitmap)
            {
                var result = await _engine.RecognizeAsync(bitmap);
                return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipt OCR failed: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
