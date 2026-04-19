using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Chandra.Ocr.Model;

public class VllmClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Settings _settings;

    public VllmClient(Settings? settings = null) => _settings = settings ?? Settings.Default;

    public async Task<List<GenerationResult>> GenerateAsync(
        List<BatchInputItem> batch,
        int? maxOutputTokens = null,
        int? maxRetries = null,
        int? maxWorkers = null,
        int? maxFailureRetries = null,
        string? vllmApiBase = null,
        IReadOnlyDictionary<string, string>? customHeaders = null,
        double temperature = 0.0,
        double topP = 0.1,
        CancellationToken ct = default)
    {
        int tokens = maxOutputTokens ?? _settings.MaxOutputTokens;
        int retries = maxRetries ?? _settings.MaxVllmRetries;
        int workers = maxWorkers ?? Math.Min(64, batch.Count);
        if (workers < 1) workers = 1;
        string apiBase = vllmApiBase ?? _settings.VllmApiBase;

        var results = new GenerationResult[batch.Count];
        using var semaphore = new SemaphoreSlim(workers);
        var tasks = new List<Task>();

        for (int i = 0; i < batch.Count; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    results[idx] = await ProcessItemAsync(batch[idx], tokens, retries,
                        maxFailureRetries, apiBase, customHeaders, temperature, topP, ct);
                }
                finally { semaphore.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<GenerationResult> ProcessItemAsync(
        BatchInputItem item, int maxTokens, int maxRetries, int? maxFailureRetries,
        string apiBase, IReadOnlyDictionary<string, string>? customHeaders,
        double temperature, double topP, CancellationToken ct)
    {
        var result = await GenerateOneAsync(item, maxTokens, apiBase, customHeaders, temperature, topP, ct);
        int attempt = 0;
        while (ShouldRetry(result, attempt, maxRetries, maxFailureRetries))
        {
            double retryTemp = Math.Min(temperature + 0.2 * (attempt + 1), 0.8);
            double retryTopP = 0.95;
            if (result.Error)
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            result = await GenerateOneAsync(item, maxTokens, apiBase, customHeaders, retryTemp, retryTopP, ct);
            attempt++;
        }
        return result;
    }

    private static bool ShouldRetry(GenerationResult result, int retries, int maxRetries, int? maxFailureRetries)
    {
        bool hasRepeat = ImageUtil.DetectRepeatToken(result.Raw) ||
                         (result.Raw.Length > 50 && ImageUtil.DetectRepeatToken(result.Raw, cutFromEnd: 50));
        if (retries < maxRetries && hasRepeat)
        {
            Console.WriteLine($"Detected repeat token, retrying generation (attempt {retries + 1})...");
            return true;
        }
        if (retries < maxRetries && result.Error)
        {
            Console.WriteLine($"Detected vLLM error, retrying generation (attempt {retries + 1})...");
            return true;
        }
        if (result.Error && maxFailureRetries.HasValue && retries < maxFailureRetries.Value)
        {
            Console.WriteLine($"Detected vLLM error, retrying generation (attempt {retries + 1})...");
            return true;
        }
        return false;
    }

    private async Task<GenerationResult> GenerateOneAsync(
        BatchInputItem item, int maxTokens, string apiBase,
        IReadOnlyDictionary<string, string>? customHeaders,
        double temperature, double topP, CancellationToken ct)
    {
        string prompt = item.Prompt ?? global::Chandra.Ocr.Prompts.PromptMapping[item.PromptType ?? "ocr_layout"];
        var image = ImageUtil.ScaleToFit(item.Image);
        string b64 = ImageToBase64Png(image);

        var request = new ChatRequest
        {
            Model = _settings.VllmModelName,
            MaxTokens = maxTokens,
            Temperature = temperature,
            TopP = topP,
            Messages = new List<ChatMessage>
            {
                new()
                {
                    Role = "user",
                    Content = new List<ChatContent>
                    {
                        new() { Type = "image_url", ImageUrl = new ImageUrl { Url = $"data:image/png;base64,{b64}" } },
                        new() { Type = "text", Text = prompt },
                    }
                }
            }
        };

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{apiBase.TrimEnd('/')}/chat/completions");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.VllmApiKey);
            if (customHeaders != null)
                foreach (var kv in customHeaders) msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            msg.Content = JsonContent.Create(request, options: JsonOpts);

            using var resp = await SharedHttp.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"Error during vLLM generation: {(int)resp.StatusCode} {body}");
                return new GenerationResult { Raw = string.Empty, TokenCount = 0, Error = true };
            }

            var payload = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
            if (payload?.Choices is not { Count: > 0 })
                return new GenerationResult { Raw = string.Empty, TokenCount = 0, Error = true };

            string raw = payload.Choices[0].Message?.ExtractContent() ?? string.Empty;
            int completionTokens = payload.Usage?.CompletionTokens ?? 0;
            return new GenerationResult { Raw = raw, TokenCount = completionTokens, Error = false };
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error during vLLM generation: {e.Message}");
            return new GenerationResult { Raw = string.Empty, TokenCount = 0, Error = true };
        }
    }

    private static string ImageToBase64Png(Image<Rgb24> image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return System.Convert.ToBase64String(ms.ToArray());
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private class ChatRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        public double Temperature { get; set; }
        [JsonPropertyName("top_p")] public double TopP { get; set; }
    }

    private class ChatMessage
    {
        public string Role { get; set; } = "user";
        public List<ChatContent> Content { get; set; } = new();
    }

    private class ChatContent
    {
        public string Type { get; set; } = "text";
        public string? Text { get; set; }
        [JsonPropertyName("image_url")] public ImageUrl? ImageUrl { get; set; }
    }

    private class ImageUrl
    {
        public string Url { get; set; } = string.Empty;
    }

    private class ChatResponse
    {
        public List<Choice>? Choices { get; set; }
        public Usage? Usage { get; set; }
    }

    private class Choice
    {
        public ResponseMessage? Message { get; set; }
    }

    private class ResponseMessage
    {
        public JsonElement Content { get; set; }

        public string ExtractContent()
        {
            if (Content.ValueKind == JsonValueKind.String) return Content.GetString() ?? string.Empty;
            if (Content.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var el in Content.EnumerateArray())
                    if (el.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                return sb.ToString();
            }
            return Content.ToString() ?? string.Empty;
        }
    }

    private class Usage
    {
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    }
}
