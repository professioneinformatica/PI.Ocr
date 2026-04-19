using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Chandra.Ocr.Model;

public static class ImageUtil
{
    public static Image<Rgb24> ScaleToFit(
        Image<Rgb24> img,
        (int W, int H) maxSize = default,
        (int W, int H) minSize = default,
        int gridSize = 28)
    {
        if (maxSize == default) maxSize = (3072, 2048);
        if (minSize == default) minSize = (1792, 28);

        int width = img.Width;
        int height = img.Height;
        if (width <= 0 || height <= 0) return img;

        double originalAr = (double)width / height;
        long currentPixels = (long)width * height;
        long maxPixels = (long)maxSize.W * maxSize.H;
        long minPixels = (long)minSize.W * minSize.H;

        double scale = 1.0;
        if (currentPixels > maxPixels)
            scale = Math.Sqrt((double)maxPixels / currentPixels);
        else if (currentPixels < minPixels)
            scale = Math.Sqrt((double)minPixels / currentPixels);

        int wBlocks = Math.Max(1, (int)Math.Round(width * scale / gridSize));
        int hBlocks = Math.Max(1, (int)Math.Round(height * scale / gridSize));

        while ((long)wBlocks * hBlocks * gridSize * gridSize > maxPixels)
        {
            if (wBlocks == 1 && hBlocks == 1) break;
            if (wBlocks == 1) { hBlocks--; continue; }
            if (hBlocks == 1) { wBlocks--; continue; }

            double arWLoss = Math.Abs((double)(wBlocks - 1) / hBlocks - originalAr);
            double arHLoss = Math.Abs((double)wBlocks / (hBlocks - 1) - originalAr);
            if (arWLoss < arHLoss) wBlocks--; else hBlocks--;
        }

        int newW = wBlocks * gridSize;
        int newH = hBlocks * gridSize;
        if (newW == width && newH == height) return img;

        var clone = img.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(newW, newH),
            Sampler = KnownResamplers.Lanczos3,
            Mode = ResizeMode.Stretch
        }));
        return clone;
    }

    public static bool DetectRepeatToken(
        string predictedTokens,
        int baseMaxRepeats = 4,
        int windowSize = 500,
        int cutFromEnd = 0,
        double scalingFactor = 3.0)
    {
        if (string.IsNullOrEmpty(predictedTokens)) return false;
        if (cutFromEnd > 0 && cutFromEnd < predictedTokens.Length)
            predictedTokens = predictedTokens[..^cutFromEnd];

        for (int seqLen = 1; seqLen <= windowSize / 2; seqLen++)
        {
            if (seqLen > predictedTokens.Length) break;
            string candidate = predictedTokens[^seqLen..];
            int maxRepeats = (int)(baseMaxRepeats * (1 + scalingFactor / seqLen));

            int repeatCount = 0;
            int pos = predictedTokens.Length - seqLen;
            if (pos < 0) continue;

            while (pos >= 0)
            {
                if (pos + seqLen > predictedTokens.Length) break;
                if (predictedTokens.AsSpan(pos, seqLen).SequenceEqual(candidate))
                {
                    repeatCount++;
                    pos -= seqLen;
                }
                else break;
            }

            if (repeatCount > maxRepeats) return true;
        }
        return false;
    }
}
