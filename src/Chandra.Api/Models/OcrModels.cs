using System.Text.Json.Serialization;

namespace Chandra.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter<OutputFormat>))]
public enum OutputFormat
{
    Json,
    Text,
    Markdown,
}

public class OcrBase64Request
{
    public string FileName { get; set; } = "document";
    public string FileBase64 { get; set; } = string.Empty;
    public OutputFormat Format { get; set; } = OutputFormat.Markdown;
    public string? PageRange { get; set; }
    public bool IncludeHeadersFooters { get; set; }
}

public class OcrResponse
{
    public string FileName { get; set; } = string.Empty;
    public OutputFormat Format { get; set; }
    public int TotalPages { get; set; }
    public int TotalTokens { get; set; }
    public List<PageResult> Pages { get; set; } = new();
}

public class PageResult
{
    public int PageNumber { get; set; }
    public int[] PageBox { get; set; } = Array.Empty<int>();
    public int TokenCount { get; set; }
    /// <summary>Base64-encoded payload. For json format: a JSON document of the chunk list. For text/markdown: the raw text.</summary>
    public string Base64 { get; set; } = string.Empty;
    public bool Error { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Detail { get; set; }
}
