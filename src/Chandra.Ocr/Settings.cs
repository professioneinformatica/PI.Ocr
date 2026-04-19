namespace Chandra.Ocr;

public class Settings
{
    public int ImageDpi { get; set; } = 192;
    public int MinPdfImageDim { get; set; } = 1024;
    public int MinImageDim { get; set; } = 1536;
    public string ModelCheckpoint { get; set; } = "datalab-to/chandra-ocr-2";
    public int MaxOutputTokens { get; set; } = 12384;
    public int BboxScale { get; set; } = 1000;

    public string VllmApiKey { get; set; } = "EMPTY";
    public string VllmApiBase { get; set; } = "http://localhost:8000/v1";
    public string VllmModelName { get; set; } = "chandra";
    public int MaxVllmRetries { get; set; } = 6;

    public static Settings Default { get; } = FromEnvironment();

    public static Settings FromEnvironment()
    {
        var s = new Settings();
        s.VllmApiKey = Environment.GetEnvironmentVariable("VLLM_API_KEY") ?? s.VllmApiKey;
        s.VllmApiBase = Environment.GetEnvironmentVariable("VLLM_API_BASE") ?? s.VllmApiBase;
        s.VllmModelName = Environment.GetEnvironmentVariable("VLLM_MODEL_NAME") ?? s.VllmModelName;
        s.ModelCheckpoint = Environment.GetEnvironmentVariable("MODEL_CHECKPOINT") ?? s.ModelCheckpoint;

        if (int.TryParse(Environment.GetEnvironmentVariable("IMAGE_DPI"), out var dpi)) s.ImageDpi = dpi;
        if (int.TryParse(Environment.GetEnvironmentVariable("MAX_OUTPUT_TOKENS"), out var mot)) s.MaxOutputTokens = mot;
        if (int.TryParse(Environment.GetEnvironmentVariable("MAX_VLLM_RETRIES"), out var mvr)) s.MaxVllmRetries = mvr;
        return s;
    }
}
