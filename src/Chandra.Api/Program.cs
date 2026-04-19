using System.Text.Json.Serialization;
using Chandra.Api.Auth;
using Chandra.Api.Models;
using Chandra.Api.Services;
using Chandra.Ocr;
using Chandra.Ocr.Model;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.Configure<Settings>(builder.Configuration.GetSection("Chandra"));
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection("Api"));
builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection("Auth"));

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<Settings>>().Value);
builder.Services.AddSingleton<InferenceManager>();
builder.Services.AddScoped<OcrService>();

builder.Services.Configure<FormOptions>(o =>
{
    var api = builder.Configuration.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();
    o.MultipartBodyLengthLimit = api.MaxUploadBytes;
});
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
{
    var api = builder.Configuration.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();
    o.Limits.MaxRequestBodySize = api.MaxUploadBytes;
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var allowed = builder.Configuration.GetSection("Api:AllowedCorsOrigins").Get<string[]>() ?? Array.Empty<string>();
    if (allowed.Length == 0) p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    else p.WithOrigins(allowed).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseMiddleware<ApiKeyMiddleware>();

var apiOpts = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/ocr", async (
        HttpRequest request,
        OcrService svc,
        CancellationToken ct) =>
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new ErrorResponse { Error = "Expected multipart/form-data." });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.BadRequest(new ErrorResponse { Error = "Missing 'file' upload." });

        if (!TryParseFormat(form["format"].ToString(), out var format))
            return Results.BadRequest(new ErrorResponse { Error = "Invalid 'format' (expected json|text|markdown)." });

        string? pageRange = EmptyToNull(form["pageRange"].ToString());
        bool includeHeadersFooters = ParseBool(form["includeHeadersFooters"].ToString(), false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(apiOpts.RequestTimeoutSeconds));

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await svc.ProcessAsync(stream, file.FileName, format, pageRange, includeHeadersFooters, cts.Token);
            return Results.Ok(result);
        }
        catch (BadHttpRequestException e)
        {
            return Results.BadRequest(new ErrorResponse { Error = e.Message });
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(title: "Request timed out", statusCode: StatusCodes.Status504GatewayTimeout);
        }
    })
    .DisableAntiforgery();

app.MapPost("/api/ocr/base64", async (
        OcrBase64Request req,
        OcrService svc,
        CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(req.FileBase64))
            return Results.BadRequest(new ErrorResponse { Error = "Missing 'fileBase64'." });

        byte[] bytes;
        try { bytes = Convert.FromBase64String(req.FileBase64); }
        catch (FormatException) { return Results.BadRequest(new ErrorResponse { Error = "Invalid base64 payload." }); }

        if (bytes.Length > apiOpts.MaxUploadBytes)
            return Results.BadRequest(new ErrorResponse { Error = $"Payload exceeds {apiOpts.MaxUploadBytes} bytes." });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(apiOpts.RequestTimeoutSeconds));

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var result = await svc.ProcessAsync(ms, req.FileName, req.Format, req.PageRange, req.IncludeHeadersFooters, cts.Token);
            return Results.Ok(result);
        }
        catch (BadHttpRequestException e)
        {
            return Results.BadRequest(new ErrorResponse { Error = e.Message });
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(title: "Request timed out", statusCode: StatusCodes.Status504GatewayTimeout);
        }
    });

app.Run();

static bool TryParseFormat(string? raw, out OutputFormat format)
{
    if (string.IsNullOrWhiteSpace(raw)) { format = OutputFormat.Markdown; return true; }
    return Enum.TryParse(raw, ignoreCase: true, out format);
}

static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
static bool ParseBool(string? s, bool def) => bool.TryParse(s, out var v) ? v : def;
