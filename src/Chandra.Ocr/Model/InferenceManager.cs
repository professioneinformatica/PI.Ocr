using Chandra.Ocr.Output;

namespace Chandra.Ocr.Model;

public class InferenceOptions
{
    public int? MaxOutputTokens { get; set; }
    public int? MaxWorkers { get; set; }
    public int? MaxRetries { get; set; }
    public int? MaxFailureRetries { get; set; }
    public bool IncludeImages { get; set; } = true;
    public bool IncludeHeadersFooters { get; set; } = false;
    public int? BboxScale { get; set; }
    public string? VllmApiBase { get; set; }
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; set; }
}

public class InferenceManager
{
    private readonly Settings _settings;
    private readonly VllmClient _client;

    public InferenceManager(Settings? settings = null)
    {
        _settings = settings ?? Settings.Default;
        _client = new VllmClient(_settings);
    }

    public async Task<List<BatchOutputItem>> GenerateAsync(
        List<BatchInputItem> batch,
        InferenceOptions? opts = null,
        CancellationToken ct = default)
    {
        opts ??= new InferenceOptions();
        int bboxScale = opts.BboxScale ?? _settings.BboxScale;

        var raw = await _client.GenerateAsync(
            batch,
            maxOutputTokens: opts.MaxOutputTokens,
            maxRetries: opts.MaxRetries,
            maxWorkers: opts.MaxWorkers,
            maxFailureRetries: opts.MaxFailureRetries,
            vllmApiBase: opts.VllmApiBase,
            customHeaders: opts.CustomHeaders,
            ct: ct);

        var output = new List<BatchOutputItem>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var result = raw[i];
            var input = batch[i];
            var chunks = HtmlParser.ParseChunks(result.Raw, input.Image, bboxScale);
            var markdown = MarkdownConverter.ParseMarkdown(result.Raw, opts.IncludeHeadersFooters, opts.IncludeImages);
            var html = HtmlParser.ParseHtml(result.Raw, opts.IncludeHeadersFooters, opts.IncludeImages);
            var images = HtmlParser.ExtractImages(result.Raw, chunks, input.Image);

            output.Add(new BatchOutputItem
            {
                Markdown = markdown,
                Html = html,
                Chunks = chunks,
                Raw = result.Raw,
                PageBox = new[] { 0, 0, input.Image.Width, input.Image.Height },
                TokenCount = result.TokenCount,
                Images = images,
                Error = result.Error,
            });
        }
        return output;
    }
}
