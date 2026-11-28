using System.Security.Cryptography;
using SkiaSharp;

namespace OokiGrader.Preprocessing;

internal static class BookSpreadViews
{
    private const int MaximumDetailHeight = 1600;

    internal static IReadOnlyList<BookSpreadDetail> Create(
        IReadOnlyList<PreprocessedPage> pages, int firstPage, int remainingBytes, CancellationToken cancellationToken)
    {
        var views = new List<BookSpreadDetail>();
        try
        {
            for (var index = 0; index < pages.Count && remainingBytes > 0 && views.Count < 30; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = pages[index];
                if (page.Width < 1200 || page.Height < 700 || (double)page.Width / page.Height is < 1.25 or > 2.1)
                    continue;
                using var thumbnail = SKBitmap.Decode(page.ThumbnailPng.Bytes);
                if (thumbnail is null || FindGutter(thumbnail) is not double gutter)
                    continue;

                using var source = SKBitmap.Decode(page.NormalizedPng.Bytes)
                    ?? throw new PreprocessingException("normalized_page_invalid", "Cannot read book spread pixels.");
                using var image = SKImage.FromBitmap(source);
                var split = (int)Math.Round(source.Width * gutter);
                var start = views.Count;
                var left = EncodePanel(image, 0, split);
                views.Add(new BookSpreadDetail(firstPage + index, "left", left));
                var right = EncodePanel(image, split, source.Width);
                views.Add(new BookSpreadDetail(firstPage + index, "right", right));
                var pairBytes = left.Bytes.Length + right.Bytes.Length;
                if (pairBytes > remainingBytes)
                {
                    CryptographicOperations.ZeroMemory(left.Bytes);
                    CryptographicOperations.ZeroMemory(right.Bytes);
                    views.RemoveRange(start, 2);
                    continue;
                }
                remainingBytes -= pairBytes;
            }
            return views;
        }
        catch
        {
            foreach (var view in views) CryptographicOperations.ZeroMemory(view.Image.Bytes);
            throw;
        }
    }

    // A book spread has print on both sides and a nearly continuous vertical
    // gutter between them. A wide page or an illustration alone is insufficient.
    private static double? FindGutter(SKBitmap thumbnail)
    {
        var width = thumbnail.Width;
        var top = thumbnail.Height / 20;
        var bottom = thumbnail.Height - top;
        var printedColumns = new int[width];
        var leftRows = 0;
        var rightRows = 0;
        for (var y = top; y < bottom; y++)
        {
            var left = 0;
            var right = 0;
            for (var x = width / 20; x < width - width / 20; x++)
            {
                if (Fingerprinting.Luminance(thumbnail.GetPixel(x, y)) > 211) continue;
                printedColumns[x]++;
                if (x < width * 2 / 5) left++;
                if (x > width * 3 / 5) right++;
            }
            if (left >= 3) leftRows++;
            if (right >= 3) rightRows++;
        }
        if (Math.Min(leftRows, rightRows) < (bottom - top) / 4) return null;

        var widest = 0;
        var midpoint = 0;
        var run = 0;
        for (var x = width * 2 / 5; x < width * 3 / 5; x++)
        {
            // Ignore isolated speckles or a few ruling lines across the gutter.
            run = printedColumns[x] <= Math.Max(1, (bottom - top) / 50) ? run + 1 : 0;
            if (run <= widest) continue;
            widest = run;
            midpoint = x + 1 - run / 2;
        }
        return widest >= Math.Max(3, width / 60) ? (double)midpoint / width : null;
    }

    private static ImageArtifact EncodePanel(SKImage source, int left, int right)
    {
        var height = Math.Min(MaximumDetailHeight, source.Height);
        var width = Math.Max(1, (int)Math.Round((double)(right - left) * height / source.Height));
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawImage(source, new SKRect(left, 0, right, source.Height),
                new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear), paint: null);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85)
            ?? throw new PreprocessingException("jpeg_encode_failed", "Cannot encode book spread detail.");
        var bytes = encoded.ToArray();
        return new ImageArtifact("image/jpeg", "jpg", width, height, bytes, Fingerprinting.Sha256(bytes));
    }
}
