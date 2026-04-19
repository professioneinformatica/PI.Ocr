using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Chandra.Ocr.Model;

public class BatchInputItem
{
    public required Image<Rgb24> Image { get; init; }
    public string? Prompt { get; init; }
    public string? PromptType { get; init; }
}

public class GenerationResult
{
    public string Raw { get; init; } = string.Empty;
    public int TokenCount { get; init; }
    public bool Error { get; init; }
}

public class LayoutBlock
{
    public required int[] Bbox { get; init; }
    public string Label { get; init; } = "block";
    public string Content { get; init; } = string.Empty;
}

public class BatchOutputItem
{
    public string Markdown { get; init; } = string.Empty;
    public string Html { get; init; } = string.Empty;
    public List<LayoutBlock> Chunks { get; init; } = new();
    public string Raw { get; init; } = string.Empty;
    public int[] PageBox { get; init; } = new[] { 0, 0, 0, 0 };
    public int TokenCount { get; init; }
    public Dictionary<string, Image<Rgb24>> Images { get; init; } = new();
    public bool Error { get; init; }
}
