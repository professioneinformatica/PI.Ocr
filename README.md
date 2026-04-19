# Chandra OCR (.NET)

Porta in .NET 8 del progetto Python [datalab-to/chandra](https://github.com/datalab-to/chandra/tree/master/chandra).

Converte PDF e immagini in HTML/Markdown/JSON con informazioni di layout, interrogando un server vLLM compatibile con l'API OpenAI che serve il modello `chandra-ocr-2`.

> Nota: è implementato solo il backend **vLLM** (HTTP OpenAI-compatible). Il backend HuggingFace locale del progetto originale richiede Python + Torch e non è portabile come tale in .NET puro.

## Struttura della solution

```
Chandra.sln
src/
├── Chandra.Ocr/        # Libreria core (input, output, modello, prompt)
│   ├── Settings.cs
│   ├── Prompts.cs
│   ├── Input/FileLoader.cs       # PDF→immagini (PDFium via PDFtoImage) + immagini
│   ├── Output/HtmlParser.cs      # parse_html, parse_layout, parse_chunks, extract_images
│   ├── Output/MarkdownConverter.cs
│   └── Model/
│       ├── Schema.cs             # BatchInputItem / GenerationResult / LayoutBlock / BatchOutputItem
│       ├── ImageUtil.cs          # scale_to_fit, detect_repeat_token
│       ├── VllmClient.cs         # Client HTTP OpenAI-compatible (retry su repeat-token)
│       └── InferenceManager.cs
└── Chandra.Cli/        # CLI `chandra`
    └── Program.cs
```

## Uso

```bash
# Build
dotnet build

# CLI
dotnet run --project src/Chandra.Cli -- <input_path> <output_path> \
  --vllm-api-base http://localhost:8000/v1 \
  --vllm-model chandra
```

Variabili d'ambiente supportate: `VLLM_API_BASE`, `VLLM_API_KEY`, `VLLM_MODEL_NAME`, `MAX_OUTPUT_TOKENS`, `MAX_VLLM_RETRIES`, `IMAGE_DPI`, `MODEL_CHECKPOINT`.

## Mappa Python → .NET

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
| `chandra/model/hf.py`         | (non portato)                                   |

## Output per documento

Per ogni file processato viene creata una sottocartella `<output>/<nome>/` contenente:
- `<nome>.md` – Markdown concatenato
- `<nome>.html` – HTML concatenato
- `<nome>_metadata.json` – metadati (pagine, token, chunks, immagini)
- `<hash>_<idx>_img.webp` – immagini estratte dai blocchi `Image`/`Figure`

## Dipendenze principali

- `SixLabors.ImageSharp` / `ImageSharp.Drawing` – manipolazione immagini
- `PDFtoImage` (PDFium + SkiaSharp) – rendering PDF
- `HtmlAgilityPack` – parsing HTML
- `System.CommandLine` – CLI
