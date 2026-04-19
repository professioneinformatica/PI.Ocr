using System.Text;
using System.Text.Json;
using Chandra.Api.Models;
using Chandra.Ocr.Input;
using Chandra.Ocr.Model;
using Chandra.Ocr.Output;
using HtmlAgilityPack;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Chandra.Api.Services;

public class OcrService
{
    private readonly InferenceManager _manager;
    private readonly ApiOptions _options;
    private readonly ILogger<OcrService> _log;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public OcrService(InferenceManager manager, IOptions<ApiOptions> options, ILogger<OcrService> log)
    {
        _manager = manager;
        _options = options.Value;
        _log = log;
    }

    public async Task<OcrResponse> ProcessAsync(
        Stream fileStream,
        string fileName,
        OutputFormat format,
        string? pageRange,
        bool includeHeadersFooters,
        CancellationToken ct)
    {
        // Persist upload to a temp file because FileLoader works with paths (PDFium + ImageSharp need seekable byte sources).
        var tempPath = Path.Combine(Path.GetTempPath(), $"chandra_{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        try
        {
            await using (var fs = File.Create(tempPath))
                await fileStream.CopyToAsync(fs, ct);

            if (!FileLoader.IsSupported(tempPath))
                throw new BadHttpRequestException($"Unsupported file type: {Path.GetExtension(fileName)}");

            List<int>? pages = string.IsNullOrWhiteSpace(pageRange) ? null : FileLoader.ParseRangeStr(pageRange);

            var images = FileLoader.LoadFile(tempPath, pages);
            if (images.Count == 0)
                throw new BadHttpRequestException("No pages could be loaded from the document.");
            if (images.Count > _options.MaxPages)
            {
                foreach (var im in images) im.Dispose();
                throw new BadHttpRequestException($"Document exceeds maximum of {_options.MaxPages} pages (got {images.Count}).");
            }

            var batch = images.Select(img => new BatchInputItem { Image = img, PromptType = "ocr_layout" }).ToList();
            var opts = new InferenceOptions
            {
                IncludeImages = _options.IncludeImages,
                IncludeHeadersFooters = includeHeadersFooters,
            };

            _log.LogInformation("OCR start: file={File} pages={Count} format={Format}", fileName, images.Count, format);
            var results = await _manager.GenerateAsync(batch, opts, ct);

            var response = new OcrResponse
            {
                FileName = fileName,
                Format = format,
                TotalPages = results.Count,
                TotalTokens = results.Sum(r => r.TokenCount),
                Pages = results.Select((r, i) => BuildPageResult(i, r, format)).ToList(),
            };

            foreach (var img in images) img.Dispose();
            foreach (var r in results) foreach (var im in r.Images.Values) im.Dispose();
            return response;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best effort */ }
        }
    }

    private static PageResult BuildPageResult(int pageIdx, BatchOutputItem r, OutputFormat format)
    {
        string payload = format switch
        {
            OutputFormat.Markdown => ImageEmbedder.InlineMarkdownImages(r.Markdown, r.Images),
            OutputFormat.Text => ToPlainText(r.Html),
            OutputFormat.Json => ToChunksJson(r),
            _ => r.Markdown,
        };

        return new PageResult
        {
            PageNumber = pageIdx + 1,
            PageBox = r.PageBox,
            TokenCount = r.TokenCount,
            Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)),
            Error = r.Error,
        };
    }

    private static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var img in doc.DocumentNode.SelectNodes("//img")?.ToList() ?? new List<HtmlNode>())
            img.Remove();
        var text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
        return System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ").Trim();
    }

    private static string ToChunksJson(BatchOutputItem r)
    {
        var chunks = r.Chunks.Select(c => new
        {
            bbox = c.Bbox,
            label = c.Label,
            content = ImageEmbedder.InlineImages(c.Content, r.Images),
        }).ToList();

        var payload = new
        {
            pageBox = r.PageBox,
            tokenCount = r.TokenCount,
            html = ImageEmbedder.InlineImages(r.Html, r.Images),
            markdown = ImageEmbedder.InlineMarkdownImages(r.Markdown, r.Images),
            chunks,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }
}
