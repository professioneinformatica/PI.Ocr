using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using Chandra.Ocr.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Chandra.Ocr.Output;

public static class HtmlParser
{
    public static string HashHtml(string html)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(html));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static string GetImageName(string html, int divIdx)
        => $"{HashHtml(html)}_{divIdx}_img.webp";

    public static Dictionary<string, Image<Rgb24>> ExtractImages(
        string html, List<LayoutBlock> chunks, Image<Rgb24> image)
    {
        var images = new Dictionary<string, Image<Rgb24>>();
        int divIdx = 0;
        foreach (var chunk in chunks)
        {
            divIdx++;
            if (chunk.Label is not ("Image" or "Figure")) continue;

            var doc = new HtmlDocument();
            doc.LoadHtml(chunk.Content);
            var img = doc.DocumentNode.SelectSingleNode("//img");
            if (img == null) continue;

            var bbox = chunk.Bbox;
            int x0 = Math.Max(0, bbox[0]);
            int y0 = Math.Max(0, bbox[1]);
            int x1 = Math.Min(image.Width, bbox[2]);
            int y1 = Math.Min(image.Height, bbox[3]);
            if (x1 <= x0 || y1 <= y0) continue;

            var rect = new Rectangle(x0, y0, x1 - x0, y1 - y0);
            var cropped = image.Clone(c => c.Crop(rect));

            var imgName = GetImageName(html, divIdx);
            images[imgName] = cropped;
        }
        return images;
    }

    public static string ParseHtml(
        string html, bool includeHeadersFooters = false, bool includeImages = true)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var topLevelDivs = doc.DocumentNode.ChildNodes
            .Where(n => n.NodeType == HtmlNodeType.Element && n.Name == "div")
            .ToList();

        var outHtml = new StringBuilder();
        int divIdx = 0;
        foreach (var div in topLevelDivs)
        {
            divIdx++;
            var label = div.GetAttributeValue("data-label", "");

            if (label == "Blank-Page") continue;
            if (!includeHeadersFooters && (label == "Page-Header" || label == "Page-Footer")) continue;
            if (!includeImages && (label == "Image" || label == "Figure")) continue;

            if (label is "Image" or "Figure")
            {
                var img = div.SelectSingleNode(".//img");
                var imgSrc = GetImageName(html, divIdx);
                if (img != null)
                {
                    img.SetAttributeValue("src", imgSrc);
                }
                else
                {
                    var newImg = HtmlNode.CreateNode($"<img src='{imgSrc}'/>");
                    div.AppendChild(newImg);
                }
            }

            if (label is not ("Image" or "Figure"))
            {
                foreach (var imgTag in div.SelectNodes(".//img")?.ToList() ?? new List<HtmlNode>())
                {
                    if (string.IsNullOrEmpty(imgTag.GetAttributeValue("src", "")))
                        imgTag.Remove();
                }
            }

            if (label == "Text")
            {
                var inner = div.InnerHtml.Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(inner, "<.+>"))
                {
                    div.InnerHtml = $"<p>{inner}</p>";
                }
            }

            outHtml.Append(div.InnerHtml);
        }
        return outHtml.ToString();
    }

    public static List<LayoutBlock> ParseLayout(string html, Image<Rgb24> image, int bboxScale = 1000)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var topLevelDivs = doc.DocumentNode.ChildNodes
            .Where(n => n.NodeType == HtmlNodeType.Element && n.Name == "div")
            .ToList();

        int width = image.Width, height = image.Height;
        double widthScaler = (double)width / bboxScale;
        double heightScaler = (double)height / bboxScale;

        var blocks = new List<LayoutBlock>();
        foreach (var div in topLevelDivs)
        {
            var label = div.GetAttributeValue("data-label", "");
            if (label == "Blank-Page") continue;

            var bboxRaw = div.GetAttributeValue("data-bbox", "");
            int[] bbox;
            try
            {
                var parts = bboxRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4) throw new Exception("Invalid bbox length");
                bbox = parts.Select(int.Parse).ToArray();
            }
            catch
            {
                Console.WriteLine($"Invalid bbox format: {bboxRaw}, defaulting to full image");
                bbox = new[] { 0, 0, 1, 1 };
            }

            bbox = new[]
            {
                Math.Max(0, (int)(bbox[0] * widthScaler)),
                Math.Max(0, (int)(bbox[1] * heightScaler)),
                Math.Min((int)(bbox[2] * widthScaler), width),
                Math.Min((int)(bbox[3] * heightScaler), height),
            };
            if (string.IsNullOrEmpty(label)) label = "block";

            var content = div.InnerHtml;
            var innerDoc = new HtmlDocument();
            innerDoc.LoadHtml(content);
            foreach (var tag in innerDoc.DocumentNode.SelectNodes("//*[@data-bbox]")?.ToList() ?? new List<HtmlNode>())
            {
                tag.Attributes["data-bbox"]?.Remove();
            }
            content = innerDoc.DocumentNode.InnerHtml;

            blocks.Add(new LayoutBlock { Bbox = bbox, Label = label, Content = content });
        }
        return blocks;
    }

    public static List<LayoutBlock> ParseChunks(string html, Image<Rgb24> image, int bboxScale = 1000)
        => ParseLayout(html, image, bboxScale);
}
