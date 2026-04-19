using PDFtoImage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace Chandra.Ocr.Input;

public static class FileLoader
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".tiff", ".bmp"
    };

    public static List<Image<Rgb24>> LoadFile(string filepath, IReadOnlyList<int>? pageRange, Settings? settings = null)
    {
        settings ??= Settings.Default;
        var ext = Path.GetExtension(filepath).ToLowerInvariant();
        if (ext == ".pdf")
            return LoadPdfImages(filepath, pageRange, settings);
        return new List<Image<Rgb24>> { LoadImage(filepath, settings.MinImageDim) };
    }

    public static Image<Rgb24> LoadImage(string filepath, int minImageDim)
    {
        var img = Image.Load<Rgb24>(filepath);
        if (img.Width < minImageDim || img.Height < minImageDim)
        {
            double scale = (double)minImageDim / Math.Min(img.Width, img.Height);
            int newW = (int)(img.Width * scale);
            int newH = (int)(img.Height * scale);
            img.Mutate(c => c.Resize(new ResizeOptions
            {
                Size = new Size(newW, newH),
                Sampler = KnownResamplers.Lanczos3,
                Mode = ResizeMode.Stretch
            }));
        }
        return img;
    }

    public static List<Image<Rgb24>> LoadPdfImages(
        string filepath, IReadOnlyList<int>? pageRange, Settings settings)
    {
        var results = new List<Image<Rgb24>>();
        byte[] pdfBytes = File.ReadAllBytes(filepath);

        int pageCount = Conversion.GetPageCount(pdfBytes);
        var sizes = Conversion.GetPageSizes(pdfBytes);

        for (int pageIdx = 0; pageIdx < pageCount; pageIdx++)
        {
            if (pageRange is { Count: > 0 } && !pageRange.Contains(pageIdx)) continue;

            var size = sizes[pageIdx];
            double minPageDim = Math.Min(size.Width, size.Height);
            double scaleDpi = settings.MinPdfImageDim / minPageDim * 72.0;
            scaleDpi = Math.Max(scaleDpi, settings.ImageDpi);

            var options = new RenderOptions(
                Dpi: (int)scaleDpi,
                Width: null,
                Height: null,
                WithAnnotations: true,
                WithFormFill: true,
                WithAspectRatio: true,
                Rotation: PdfRotation.Rotate0,
                AntiAliasing: PdfAntiAliasing.All,
                BackgroundColor: null,
                Bounds: null,
                UseTiling: false,
                DpiRelativeToBounds: false,
                Grayscale: false);

            using var skBitmap = Conversion.ToImage(pdfBytes, page: pageIdx, password: null, options: options);
            results.Add(SkBitmapToImageSharp(skBitmap));
        }
        return results;
    }

    private static Image<Rgb24> SkBitmapToImageSharp(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return Image.Load<Rgb24>(ms);
    }

    public static List<int> ParseRangeStr(string rangeStr)
    {
        var pages = new HashSet<int>();
        foreach (var part in rangeStr.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                var bits = trimmed.Split('-');
                int start = int.Parse(bits[0]);
                int end = int.Parse(bits[1]);
                for (int i = start; i <= end; i++) pages.Add(i);
            }
            else
            {
                pages.Add(int.Parse(trimmed));
            }
        }
        return pages.OrderBy(p => p).ToList();
    }

    public static bool IsSupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() == ".pdf" ||
        ImageExtensions.Contains(Path.GetExtension(path));
}
