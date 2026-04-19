using Microsoft.Extensions.Options;

namespace Chandra.Api.Auth;

public class ApiKeyOptions
{
    public const string HeaderName = "X-Api-Key";
    public List<string> ApiKeys { get; set; } = new();
    public List<string> BypassPaths { get; set; } = new() { "/health", "/openapi", "/swagger" };
}

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _keys;
    private readonly string[] _bypass;

    public ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
    {
        _next = next;
        _keys = new HashSet<string>(options.Value.ApiKeys, StringComparer.Ordinal);
        _bypass = options.Value.BypassPaths.ToArray();
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (_bypass.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(ctx);
            return;
        }

        if (_keys.Count == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "No API keys configured on the server." });
            return;
        }

        if (!ctx.Request.Headers.TryGetValue(ApiKeyOptions.HeaderName, out var provided) ||
            !_keys.Contains(provided.ToString()))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key." });
            return;
        }

        await _next(ctx);
    }
}
