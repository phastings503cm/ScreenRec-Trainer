using SkiaSharp;

namespace ScreenRec.Core.Recording;

public static class ImageTools
{
    /// <summary>A 32×18 grey thumbnail, cheap to compare.</summary>
    public static byte[] Signature(SKBitmap bitmap)
    {
        using var small = bitmap.Resize(new SKImageInfo(32, 18, SKColorType.Gray8, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear));
        if (small is null) return new byte[32 * 18];
        return small.Bytes;
    }

    /// <summary>0 = same picture, 1 = nothing alike.</summary>
    public static double Difference(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) return 1;
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (255.0 * a.Length);
    }

    public static byte[] Jpeg(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 20, 100));
        return data.ToArray();
    }

    /// <summary>A JPEG file scaled so its longest edge is at most <paramref name="maxEdge"/>, optionally with a ring where the
    /// person clicked (normalized coordinates) — so the model sees exactly what was clicked.</summary>
    public static byte[] PrepareForModel(string jpegPath, int maxEdge, IEnumerable<(double X, double Y)>? marks = null, int quality = 75)
    {
        using var source = SKBitmap.Decode(jpegPath) ?? throw new InvalidDataException($"Not an image: {jpegPath}");
        var scale = Math.Min(1.0, maxEdge / (double)Math.Max(source.Width, source.Height));
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var bitmap = scale < 1 ? source.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) : source.Copy();
        if (marks is not null)
        {
            using var canvas = new SKCanvas(bitmap);
            using var ring = new SKPaint { Color = new SKColor(255, 40, 40, 230), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, w / 300f), IsAntialias = true };
            foreach (var (x, y) in marks)
                canvas.DrawCircle((float)(x * w), (float)(y * h), Math.Max(14, w / 60f), ring);
        }
        return Jpeg(bitmap, quality);
    }
}
