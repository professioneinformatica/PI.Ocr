using System.CommandLine;
using System.Text.Json;
using Chandra.Ocr;
using Chandra.Ocr.Input;
using Chandra.Ocr.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

var inputArg = new Argument<string>("input_path") { Description = "Path to a file or directory of PDFs/images." };
var outputArg = new Argument<string>("output_path") { Description = "Directory where outputs will be saved." };
var pageRangeOpt = new Option<string?>("--page-range") { Description = "Page range for PDFs (e.g. '1-5,7,9-12')." };
var maxTokensOpt = new Option<int?>("--max-output-tokens") { Description = "Maximum output tokens per page." };
var maxWorkersOpt = new Option<int?>("--max-workers") { Description = "Max parallel workers for vLLM inference." };
var maxRetriesOpt = new Option<int?>("--max-retries") { Description = "Max retries for vLLM inference." };
var includeImagesOpt = new Option<bool>("--include-images") { DefaultValueFactory = _ => true, Description = "Include images in output." };
var includeHeadersOpt = new Option<bool>("--include-headers-footers") { DefaultValueFactory = _ => false, Description = "Include page headers/footers." };
var saveHtmlOpt = new Option<bool>("--save-html") { DefaultValueFactory = _ => true, Description = "Save HTML output files." };
var batchSizeOpt = new Option<int?>("--batch-size") { Description = "Number of pages per batch." };
var paginateOpt = new Option<bool>("--paginate-output") { Description = "Add a page separator between pages." };
var apiBaseOpt = new Option<string?>("--vllm-api-base") { Description = "Override VLLM API base URL (e.g. http://localhost:8000/v1)." };
var modelNameOpt = new Option<string?>("--vllm-model") { Description = "Override VLLM model name." };

var root = new RootCommand("Chandra OCR - converts PDFs/images into HTML/Markdown via a vLLM server.")
{
    inputArg,
    outputArg,
    pageRangeOpt,
    maxTokensOpt,
    maxWorkersOpt,
    maxRetriesOpt,
    includeImagesOpt,
    includeHeadersOpt,
    saveHtmlOpt,
    batchSizeOpt,
    paginateOpt,
    apiBaseOpt,
    modelNameOpt,
};

root.SetAction(async (parseResult, ct) =>
{
    var input = parseResult.GetValue(inputArg)!;
    var output = parseResult.GetValue(outputArg)!;
    var pageRange = parseResult.GetValue(pageRangeOpt);
    var maxTokens = parseResult.GetValue(maxTokensOpt);
    var maxWorkers = parseResult.GetValue(maxWorkersOpt);
    var maxRetries = parseResult.GetValue(maxRetriesOpt);
    var includeImages = parseResult.GetValue(includeImagesOpt);
    var includeHeaders = parseResult.GetValue(includeHeadersOpt);
    var saveHtml = parseResult.GetValue(saveHtmlOpt);
    var batchSize = parseResult.GetValue(batchSizeOpt) ?? 28;
    var paginate = parseResult.GetValue(paginateOpt);
    var apiBase = parseResult.GetValue(apiBaseOpt);
    var modelName = parseResult.GetValue(modelNameOpt);

    var settings = Settings.FromEnvironment();
    if (!string.IsNullOrEmpty(apiBase)) settings.VllmApiBase = apiBase;
    if (!string.IsNullOrEmpty(modelName)) settings.VllmModelName = modelName;

    Console.WriteLine("Chandra CLI - Starting OCR processing");
    Console.WriteLine($"Input:   {input}");
    Console.WriteLine($"Output:  {output}");
    Console.WriteLine($"Method:  vllm");
    Console.WriteLine($"Server:  {settings.VllmApiBase}");

    Directory.CreateDirectory(output);

    var inputPath = Path.GetFullPath(input);
    var files = GetSupportedFiles(inputPath);
    Console.WriteLine($"\nFound {files.Count} file(s) to process.");
    if (files.Count == 0) return 0;

    var manager = new InferenceManager(settings);
    List<int>? pageList = pageRange is null ? null : FileLoader.ParseRangeStr(pageRange);

    for (int i = 0; i < files.Count; i++)
    {
        var file = files[i];
        Console.WriteLine($"\n[{i + 1}/{files.Count}] Processing: {Path.GetFileName(file)}");
        try
        {
            var images = FileLoader.LoadFile(file, pageList, settings);
            Console.WriteLine($"  Loaded {images.Count} page(s)");

            var allResults = new List<BatchOutputItem>();
            for (int start = 0; start < images.Count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, images.Count);
                var batch = images.GetRange(start, end - start)
                    .Select(img => new BatchInputItem { Image = img, PromptType = "ocr_layout" })
                    .ToList();

                Console.WriteLine($"  Processing pages {start + 1}-{end}...");
                var opts = new InferenceOptions
                {
                    IncludeImages = includeImages,
                    IncludeHeadersFooters = includeHeaders,
                    MaxOutputTokens = maxTokens,
                    MaxWorkers = maxWorkers,
                    MaxRetries = maxRetries,
                };
                var batchResults = await manager.GenerateAsync(batch, opts, ct);
                allResults.AddRange(batchResults);
            }

            SaveMergedOutput(output, Path.GetFileName(file), allResults, includeImages, saveHtml, paginate);
            Console.WriteLine($"  Completed: {Path.GetFileName(file)}");

            foreach (var img in images) img.Dispose();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  Error processing {Path.GetFileName(file)}: {e.Message}");
        }
    }

    Console.WriteLine($"\nProcessing complete. Results saved to: {output}");
    return 0;
});

return await root.Parse(args).InvokeAsync();

static List<string> GetSupportedFiles(string path)
{
    var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".tiff", ".bmp" };

    if (File.Exists(path))
    {
        if (supported.Contains(Path.GetExtension(path))) return new List<string> { path };
        throw new InvalidOperationException($"Unsupported file type: {Path.GetExtension(path)}");
    }
    if (Directory.Exists(path))
    {
        return Directory.EnumerateFiles(path)
            .Where(f => supported.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();
    }
    throw new FileNotFoundException($"Path does not exist: {path}");
}

static void SaveMergedOutput(
    string outputDir, string fileName, List<BatchOutputItem> results,
    bool saveImages, bool saveHtml, bool paginate)
{
    string safeName = Path.GetFileNameWithoutExtension(fileName);
    string fileOutputDir = Path.Combine(outputDir, safeName);
    Directory.CreateDirectory(fileOutputDir);

    var allMd = new List<string>();
    var allHtml = new List<string>();
    var allMeta = new List<object>();
    int totalTokens = 0, totalChunks = 0, totalImages = 0;

    for (int pageNum = 0; pageNum < results.Count; pageNum++)
    {
        var r = results[pageNum];
        if (pageNum > 0 && paginate)
        {
            allMd.Add($"\n\n{pageNum}" + new string('-', 48) + "\n\n");
            allHtml.Add($"\n\n<!-- Page {pageNum + 1} -->\n\n");
        }
        allMd.Add(r.Markdown);
        allHtml.Add(r.Html);

        totalTokens += r.TokenCount;
        totalChunks += r.Chunks.Count;
        totalImages += r.Images.Count;

        allMeta.Add(new
        {
            page_num = pageNum,
            page_box = r.PageBox,
            token_count = r.TokenCount,
            num_chunks = r.Chunks.Count,
            num_images = r.Images.Count,
        });

        if (saveImages && r.Images.Count > 0)
        {
            foreach (var (name, img) in r.Images)
            {
                string imgPath = Path.Combine(fileOutputDir, name);
                img.Save(imgPath, new WebpEncoder());
                img.Dispose();
            }
        }
    }

    File.WriteAllText(Path.Combine(fileOutputDir, $"{safeName}.md"), string.Concat(allMd));
    if (saveHtml)
        File.WriteAllText(Path.Combine(fileOutputDir, $"{safeName}.html"), string.Concat(allHtml));

    var metadata = new
    {
        file_name = fileName,
        num_pages = results.Count,
        total_token_count = totalTokens,
        total_chunks = totalChunks,
        total_images = totalImages,
        pages = allMeta,
    };
    var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(fileOutputDir, $"{safeName}_metadata.json"), json);

    Console.WriteLine($"  Saved: {Path.Combine(fileOutputDir, $"{safeName}.md")} ({results.Count} page(s))");
}
