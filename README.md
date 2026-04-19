# Chandra OCR (.NET)

.NET 10 port of the Python project [datalab-to/chandra](https://github.com/datalab-to/chandra/tree/master/chandra).

Converts PDFs and images to HTML/Markdown/JSON with layout information by calling a vLLM server that exposes an OpenAI-compatible API and serves the `chandra-ocr-2` model.

> Note: only the **vLLM** backend is implemented (HTTP, OpenAI-compatible). The upstream local HuggingFace backend requires Python + Torch and cannot be ported as-is to pure .NET.

## Solution layout

```
Chandra.sln
src/
├── Chandra.Ocr/        # Core library (input, output, model, prompts)
│   ├── Settings.cs
│   ├── Prompts.cs
│   ├── Input/FileLoader.cs       # PDF → images (PDFium via PDFtoImage) + images
│   ├── Output/HtmlParser.cs      # parse_html, parse_layout, parse_chunks, extract_images
│   ├── Output/MarkdownConverter.cs
│   └── Model/
│       ├── Schema.cs             # BatchInputItem / GenerationResult / LayoutBlock / BatchOutputItem
│       ├── ImageUtil.cs          # scale_to_fit, detect_repeat_token
│       ├── VllmClient.cs         # OpenAI-compatible HTTP client (retry on repeat-token)
│       └── InferenceManager.cs
├── Chandra.Cli/        # `chandra` CLI
│   └── Program.cs
└── Chandra.Api/        # ASP.NET Core Minimal API
    ├── Program.cs
    ├── Auth/ApiKeyMiddleware.cs
    ├── Models/OcrModels.cs
    └── Services/OcrService.cs
```

## Usage

```bash
# Build
dotnet build

# CLI
dotnet run --project src/Chandra.Cli -- <input_path> <output_path> \
  --vllm-api-base http://localhost:8000/v1 \
  --vllm-model chandra

# API
dotnet run --project src/Chandra.Api
# -> http://localhost:5000 (or whatever appsettings/launchSettings says)
```

### HTTP API

Two equivalent endpoints; the response is always JSON:

```bash
# Multipart
curl -X POST http://localhost:5000/api/ocr \
  -H "X-Api-Key: <key>" \
  -F "file=@doc.pdf" \
  -F "format=markdown" \
  -F "pageRange=1-3"

# JSON + base64
curl -X POST http://localhost:5000/api/ocr/base64 \
  -H "X-Api-Key: <key>" \
  -H "Content-Type: application/json" \
  -d '{"fileName":"doc.pdf","fileBase64":"<base64>","format":"json"}'
```

`format` ∈ `json | text | markdown`. The response carries `pages[].base64`, whose decoded content is:
- `markdown` — markdown with images inlined as `data:image/webp;base64,...`
- `text` — plain text (images stripped)
- `json` — per-page serialized JSON with `chunks[]`, `html`, `markdown` (images inlined)

API config (`appsettings.json` → `Api` section): `MaxPages`, `RequestTimeoutSeconds`, `MaxUploadBytes`, `IncludeImages`, `AllowedCorsOrigins`. API keys under `Auth:ApiKeys` (array).

Supported environment variables: `VLLM_API_BASE`, `VLLM_API_KEY`, `VLLM_MODEL_NAME`, `MAX_OUTPUT_TOKENS`, `MAX_VLLM_RETRIES`, `IMAGE_DPI`, `MODEL_CHECKPOINT`.

## Python → .NET map

| Python                        | .NET                                            |
|-------------------------------|-------------------------------------------------|
| `chandra/settings.py`         | `Settings.cs`                                   |
| `chandra/prompts.py`          | `Prompts.cs`                                    |
| `chandra/input.py`            | `Input/FileLoader.cs`                           |
| `chandra/output.py`           | `Output/HtmlParser.cs` + `MarkdownConverter.cs` |
| `chandra/model/schema.py`     | `Model/Schema.cs`                               |
| `chandra/model/util.py`       | `Model/ImageUtil.cs`                            |
| `chandra/model/vllm.py`       | `Model/VllmClient.cs`                           |
| `chandra/model/__init__.py`   | `Model/InferenceManager.cs`                     |
| `chandra/scripts/cli.py`      | `Chandra.Cli/Program.cs`                        |
| `chandra/model/hf.py`         | (not ported)                                    |

## Per-document output

For each processed file a `<output>/<name>/` sub-directory is created containing:
- `<name>.md` — concatenated Markdown
- `<name>.html` — concatenated HTML
- `<name>_metadata.json` — metadata (pages, tokens, chunks, images)
- `<hash>_<idx>_img.webp` — images extracted from `Image`/`Figure` blocks

## Main dependencies

- `SixLabors.ImageSharp` / `ImageSharp.Drawing` — image manipulation
- `PDFtoImage` (PDFium + SkiaSharp) — PDF rendering
- `HtmlAgilityPack` — HTML parsing
- `System.CommandLine` — CLI
