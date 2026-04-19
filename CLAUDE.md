# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 10 port of the Python project [datalab-to/chandra](https://github.com/datalab-to/chandra/tree/master/chandra). It converts PDFs and images into HTML/Markdown/JSON **with layout information** by delegating the OCR to a vLLM server that exposes an OpenAI-compatible `/chat/completions` endpoint and serves the `chandra-ocr-2` vision model.

Only the **vLLM backend** is ported. The upstream HuggingFace local backend (`chandra/model/hf.py`) is intentionally omitted — it requires Python + Torch and has no clean .NET equivalent.

## Commands

```bash
dotnet build                                        # build all three projects
dotnet run --project src/Chandra.Cli -- --help      # CLI usage
dotnet run --project src/Chandra.Cli -- <input> <output> \
  --vllm-api-base http://localhost:8000/v1 \
  --vllm-model chandra
dotnet run --project src/Chandra.Api                # ASP.NET Core API (reads appsettings.json)
```

No tests yet. No linter configured beyond the .NET SDK default analyzers.

Environment variables (override `Settings` defaults): `VLLM_API_BASE`, `VLLM_API_KEY`, `VLLM_MODEL_NAME`, `MAX_OUTPUT_TOKENS`, `MAX_VLLM_RETRIES`, `IMAGE_DPI`, `MODEL_CHECKPOINT`.

## Rules

### Language

- **Every artifact in this repo is written in English.** Code, identifiers, comments, XML docs, log messages, CLI strings, API error strings, commit messages, PR descriptions, `README.md`, `CLAUDE.md`, `.http` fixtures — all English. The user types in Italian in chat; do not let that leak into files.
- Apply even when the user phrases the request in Italian. They expect English output on disk.

### Code conventions used here

- **File-scoped namespaces**, `Nullable` and `ImplicitUsings` enabled on every project. Match that — do not revert to block-scoped or suppress nullability.
- **Helpers are `static class`es** grouped by concern (`HtmlParser`, `MarkdownConverter`, `ImageEmbedder`, `FileLoader`, `ImageUtil`). Don't turn them into DI services unless state appears.
- **Data types use `init;` setters**, not constructors, not records (`Schema.cs`, `OcrModels.cs`). Callers use object initializers. Follow the same shape when adding DTOs.
- **No XML docstrings by default.** The one exception already in tree is `PageResult.Base64` (documents non-obvious semantics). Only add one when meaning cannot be inferred from the signature.
- **Cancellation tokens are plumbed through every async call** that eventually touches HTTP or IO (`InferenceManager.GenerateAsync`, `VllmClient`, `OcrService.ProcessAsync`). Don't drop them on the floor in new async methods.
- **`Image<Rgb24>` lifecycle is manual.** ImageSharp images are not DI-managed. Dispose them explicitly (see `OcrService.ProcessAsync` and `Chandra.Cli.Program`). A leaked page image is a leaked ~20–40 MB buffer.

### DI / options patterns (Chandra.Api)

- **Config binds to plain POCOs** via `builder.Services.Configure<T>(Configuration.GetSection("..."))`: `Settings` ← `Chandra`, `ApiOptions` ← `Api`, `ApiKeyOptions` ← `Auth`. Keep new config in the same shape — don't invent per-class `IConfiguration` lookups.
- **`Settings` is registered as a singleton via `IOptions<Settings>.Value`** so constructors in `Chandra.Ocr` can take it directly without depending on `Microsoft.Extensions.Options`. Mirror that when exposing new core types through DI.
- **`InferenceManager` is a singleton**, `OcrService` is scoped. Anything that owns an `HttpClient` or is expensive to build → singleton.
- **Hard limits are enforced in `OcrService` before calling `InferenceManager`**, not in middleware. Add new request-level caps in the same place.

### Error handling

- Throw `BadHttpRequestException` from service code for 4xx conditions — the endpoint handlers already translate it into `Results.BadRequest(new ErrorResponse { ... })`. Don't leak other exception types to clients.
- Timeouts surface as `OperationCanceledException` → translated to `504 Gateway Timeout`. Don't swallow them.
- The ApiKey middleware returns **500** (not 401) when `Auth:ApiKeys` is empty — that's intentional: empty-keys is a misconfigured server, not an unauthenticated caller.

### When porting upstream fixes

- Find the matching Python file in the parity table below and port the logic there, not elsewhere. Keep method names close to the Python originals so future diffs stay legible.
- If upstream adds a new allowed HTML tag to the prompt, also teach `MarkdownConverter.ConvertNode` how to render it — the converter is hand-rolled, not `markdownify`.

## Architecture: the end-to-end pipeline

The whole system is a single linear pipeline. Understanding that pipeline is the key to being productive here — individual files are thin.

```
File(s) ──► FileLoader ──► Image<Rgb24>[]  (one per page)
                                │
                                ▼
                         ImageUtil.ScaleToFit   (grid-aligned 28×28 blocks, pixel-budget clamp)
                                │
                                ▼
                         VllmClient   ── POST /chat/completions  ──► raw HTML string
                    (base64 PNG + OCR_LAYOUT_PROMPT)              ◄── retry on repeat-token
                                │
                                ▼
             ┌──── HtmlParser.ParseChunks  ──► List<LayoutBlock>
 raw HTML ───┼──── HtmlParser.ParseHtml    ──► cleaned HTML
             ├──── MarkdownConverter       ──► Markdown
             └──── HtmlParser.ExtractImages──► Dictionary<name, Image<Rgb24>>
                                │
                                ▼
                         BatchOutputItem   (assembled by InferenceManager)
                                │
                                ▼
                         Chandra.Cli.SaveMergedOutput
                                │
                                ▼
             <output>/<stem>/{<stem>.md, <stem>.html, <stem>_metadata.json, *_img.webp}
```

`InferenceManager.GenerateAsync` is the single orchestration seam. Everything above it is IO/prep; everything below it is HTML post-processing.

## Things that are non-obvious

- **Prompt contract, not code contract.** The model is instructed (in `Prompts.cs`) to emit HTML where each top-level `<div>` has `data-bbox="x0 y0 x1 y1"` (normalized 0–`BboxScale`, default 1000) and `data-label="<block-type>"`. `HtmlParser.ParseLayout` rescales bboxes from that normalized space into actual pixel coordinates of the *scaled* image (not the original). If you change the prompt, the parser must change too.

- **Image scaling is load-bearing.** `ImageUtil.ScaleToFit` aligns to a 28-pixel grid (the Qwen-family vision patch size) and enforces `(3072, 2048)` pixel-budget max. Pages are scaled *before* being sent to the model — the bbox coordinates the model returns are relative to that scaled image, so `ExtractImages` crops from the scaled image, not the original.

- **Retry policy is tuned to a failure mode.** `VllmClient.ShouldRetry` calls `ImageUtil.DetectRepeatToken` on the model output — the model occasionally loops and emits a repeating tail. Retries bump temperature (up to 0.8) and top_p (to 0.95) to break the loop. That's why temperature defaults to 0.0 on first try.

- **`Prompts` name-resolution quirk.** The root namespace is `Chandra.Ocr` and there's also a top-level class `Prompts` in it. From inside `Chandra.Ocr.Model`, `Prompts` does not resolve — use `global::Chandra.Ocr.Prompts` (see `VllmClient.GenerateOneAsync`).

- **Python parity mapping** (useful when porting bug fixes from upstream):

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

- **PDF rendering is not thread-safe.** `PDFtoImage`/PDFium serializes all calls internally. Page loading in `FileLoader.LoadPdfImages` is intentionally sequential; parallelism happens only downstream in `VllmClient` (per-request `SemaphoreSlim`).

- **Markdown converter is hand-rolled**, not a port of `markdownify`. Tables are emitted as raw HTML (matching upstream behavior). `<math display="block">` → `$$...$$`; inline `<math>` → `$...$`. If you add an HTML tag to the prompt's allowed list, also teach `MarkdownConverter.ConvertNode` how to handle it.

## Chandra.Api — the HTTP layer

Minimal API (`src/Chandra.Api`) wrapping `InferenceManager` as a singleton. Two endpoints accept the same work: `POST /api/ocr` (multipart with `file`) and `POST /api/ocr/base64` (JSON body with `fileBase64`). Both go through `OcrService.ProcessAsync` which writes the upload to a temp file, because `FileLoader` works on paths (PDFium needs a seekable source).

- **Response shape is invariant.** Always `OcrResponse { fileName, format, totalPages, totalTokens, pages[] }`. Each `PageResult.Base64` holds the actual payload encoded in the requested format — this is by design (the user wanted one stable wire format).
- **Format dispatch** happens in `OcrService.BuildPageResult`: `markdown` → `ImageEmbedder.InlineMarkdownImages`, `json` → `ToChunksJson` (serialized chunks + inlined html/markdown), `text` → `ToPlainText` (HTML → `InnerText`, images stripped because they can't be meaningfully inlined in plain text).
- **Image inlining matters.** Core pipeline emits `<img src="<hash>_<idx>_img.webp">` (see `HtmlParser.GetImageName`) and a parallel `BatchOutputItem.Images` dictionary keyed by the same name. `ImageEmbedder` is what reconnects them as `data:image/webp;base64,...` URIs. If you change the image naming scheme in `HtmlParser`, update the regex in `ImageEmbedder`.
- **Auth.** `ApiKeyMiddleware` checks `X-Api-Key` against `Auth:ApiKeys` in config. Paths in `Auth:BypassPaths` skip the check (default: `/health`, `/openapi`, `/swagger`). If `ApiKeys` is empty the middleware returns 500 — misconfigured server, not unauthenticated — so production configs *must* set at least one key.
- **Hard limits** (config `Api:` section): `MaxPages` (rejected in `OcrService` before inference), `RequestTimeoutSeconds` (linked CTS per request), `MaxUploadBytes` (applied to both Kestrel `MaxRequestBodySize` and `FormOptions.MultipartBodyLengthLimit`).
- **vLLM is pinned to config**, never overridable per request. The API reads the `Chandra` section of `appsettings.json` into `Settings` and registers it as a singleton.
