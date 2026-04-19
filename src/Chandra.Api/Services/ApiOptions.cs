namespace Chandra.Api.Services;

public class ApiOptions
{
    public int MaxPages { get; set; } = 50;
    public int RequestTimeoutSeconds { get; set; } = 600;
    public long MaxUploadBytes { get; set; } = 100L * 1024 * 1024;
    public bool IncludeImages { get; set; } = true;
    public List<string> AllowedCorsOrigins { get; set; } = new();
}
