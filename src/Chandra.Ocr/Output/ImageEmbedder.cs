using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Chandra.Ocr.Output;

public static class ImageEmbedder
{
    private static readonly Regex SrcPattern = new(
        @"src=([""'])(?<name>[^""']+?_img\.webp)\1",
        RegexOptions.Compiled);

    public static string InlineImages(string content, IReadOnlyDictionary<string, Image<Rgb24>> images)
    {
        if (string.IsNullOrEmpty(content) || images.Count == 0) return content;

        var encoder = new WebpEncoder();
        var cache = new Dictionary<string, string>();

        return SrcPattern.Replace(content, match =>
        {
            var name = match.Groups["name"].Value;
            if (!images.TryGetValue(name, out var img)) return match.Value;

            if (!cache.TryGetValue(name, out var dataUri))
            {
                using var ms = new MemoryStream();
                img.Save(ms, encoder);
                dataUri = $"data:image/webp;base64,{Convert.ToBase64String(ms.ToArray())}";
                cache[name] = dataUri;
            }
            return $"src=\"{dataUri}\"";
        });
    }

    public static string InlineMarkdownImages(string markdown, IReadOnlyDictionary<string, Image<Rgb24>> images)
    {
        if (string.IsNullOrEmpty(markdown) || images.Count == 0) return markdown;

        var encoder = new WebpEncoder();
        var cache = new Dictionary<string, string>();

        return Regex.Replace(markdown, @"!\[(?<alt>[^\]]*)\]\((?<name>[^)]+?_img\.webp)\)", match =>
        {
            var name = match.Groups["name"].Value;
            if (!images.TryGetValue(name, out var img)) return match.Value;

            if (!cache.TryGetValue(name, out var dataUri))
            {
                using var ms = new MemoryStream();
                img.Save(ms, encoder);
                dataUri = $"data:image/webp;base64,{Convert.ToBase64String(ms.ToArray())}";
                cache[name] = dataUri;
            }
            return $"![{match.Groups["alt"].Value}]({dataUri})";
        });
    }
}
