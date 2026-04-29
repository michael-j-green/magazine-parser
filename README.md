# Magazine Parser

Parse scanned magazine PDFs into structured JSON using:

- PDF page rendering
- Tesseract OCR
- Ollama model inference (per stage)

## Arguments

| Argument | Required | Default | Description |
|---|---|---|---|
| `<pdf-path>` | Yes | — | Path to the PDF file to parse. |
| `-o`, `--output <directory>` | | `./<pdf-name>/` in current directory | Output directory for all artefacts. |
| `--profile <name>` | | — | Preset model profile: `faster`, `balanced`, or `quality`. Individual `--ollama-*-model` flags override this. |
| `--ollama-endpoint <uri>` | | `http://127.0.0.1:11434` | Ollama API endpoint. |
| `--ollama-model <model>` | | `gemma3:12b` | Fallback model for all stages not otherwise overridden. |
| `--ollama-page-analysis-model <model>` | | `--ollama-model` | Vision-capable model used for page layout analysis and classification. |
| `--ollama-metadata-model <model>` | | `--ollama-model` | Model used for magazine metadata extraction. |
| `--ollama-grouping-model <model>` | | `--ollama-model` | Model used for grouping pages into article boundaries. |
| `--ollama-article-analysis-model <model>` | | `--ollama-model` | Model used for final article content analysis and summarization. |
| `--ollama-timeout <seconds>` | | `900` | Per-request HTTP timeout. Retried up to 3 times with exponential backoff. |
| `--tessdata-path <path>` | | `./tessdata` (submodule) | Directory containing Tesseract language data files (`.traineddata`). |
| `--tesseract-language <id>` | | `eng` | Tesseract language code (e.g. `eng`, `fra`, `deu`). |
| `--render-scale <number>` | | `2.5` | PDF render scale. Higher values increase DPI and OCR quality at the cost of memory. |
| `--max-pages <number>` | | — | Stop after processing this many pages. Useful for testing. |
| `--summary-only` | | `false` | Skip article grouping and article analysis stages. |
| `--progress` | | `false` | Emit machine-readable JSON progress events to stdout. |
| `-h`, `--help` | | | Show usage information and exit. |

## Output Structure

Every run writes its artefacts into the output directory:

```
<output-dir>/
  <pdf-name>.json       ← structured JSON output
  ocr.txt               ← full OCR transcript of every page
  pages/
    page-001.jpg        ← every rendered page as a JPEG
    page-002.jpg
    ...
```

Image paths inside the JSON (e.g. `coverImagePath`, `logoImagePath`, article `images[].filePath`) are relative to the output directory, so they resolve correctly as `<output-dir>/pages/page-NNN.jpg`.

## JSON Output Schema

```jsonc
{
  "sourcePdfPath": "C:/path/to/magazine.pdf",
  "name": "Your Commodore",
  "releaseDate": "February 1988",          // free-form date string as printed
  "publisher": "Argus Specialist Publications",
  "type": "computing",                     // see MagazineType enum
  "countryOfOrigin": "UK",
  "originalPrice": "£1.50",
  "coverImagePath": "pages/page-001.jpg",  // null if not detected
  "logoImagePath": "pages/page-004.jpg",   // null if not detected
  "pageCount": 109,
  "articles": [
    {
      "title": "Sprite Magic",
      "byline": "John Smith",              // null if not detected
      "subtitle": "A complete guide to sprite animation",
      "type": "feature",                   // see ArticleType enum
      "startPage": 12,
      "endPage": 15,
      "summary": "...",
      "tags": ["graphics", "programming", "sprites"],
      "sentiment": "positive",             // see SentimentLabel enum
      "sentimentScore": 0.72,              // 0-1, null if not applicable
      "ratings": [                         // empty array if no ratings
        { "label": "Overall", "value": 8, "scale": 10, "evidence": "..." }
      ],
      "reviewedItemReleaseDate": null,     // for review articles only
      "reviewedItemPrice": null,           // for review articles only
      "images": [                          // photos/illustrations on article pages
        {
          "pageNumber": 12,
          "filePath": "pages/page-012.jpg",
          "caption": "Sprite editor screenshot",
          "description": "A screenshot showing the Commodore sprite editor"
        }
      ],
      "text": "Full cleaned OCR body text..."
    }
  ]
}
```

### Enum values

| Enum | Values |
|---|---|
| `MagazineType` | `unknown`, `computing`, `gaming`, `technology`, `music`, `film`, `fashion`, `news`, `generalInterest`, `hobby`, `lifestyle`, `business`, `other` |
| `ArticleType` | `unknown`, `editorial`, `news`, `feature`, `interview`, `review`, `preview`, `guide`, `letters`, `advertisement`, `contents`, `other` |
| `SentimentLabel` | `unknown`, `veryNegative`, `negative`, `mixed`, `positive`, `veryPositive`, `neutral` |

## Recommended Models

| Stage | Recommended Model | Why |
|---|---|---|
| Page analysis (vision) | `llava:13b` | Strong at image-grounded layout understanding and page classification. |
| Metadata extraction | `qwen2.5:7b` | Fast and consistent for short structured outputs. |
| Article grouping | `qwen2.5:7b` | Good reasoning quality with lower latency for boundary decisions. |
| Article analysis | `mistral:7b` | Solid long-form summarization and interpretation quality. |

```powershell
.\magparse\bin\Debug\net10.0\magparse.exe C:\path\to\magazine.pdf `
  -o .\output\my-magazine `
  --profile balanced
```

You can override any individual stage by adding the corresponding flag alongside `--profile`:

```powershell
# Use the balanced profile but swap in a larger model for article analysis
.\magparse\bin\Debug\net10.0\magparse.exe C:\path\to\magazine.pdf `
  --profile balanced `
  --ollama-article-analysis-model mistral:instruct
```

### Profile comparison

| Profile | Page Analysis (Vision) | Metadata | Grouping | Article Analysis | Tradeoff |
|---|---|---|---|---|---|
| `faster` | `llava:7b` | `qwen2.5:3b` | `qwen2.5:3b` | `qwen2.5:7b` | Fastest runtime, lower accuracy on harder pages. |
| `balanced` (recommended) | `llava:13b` | `qwen2.5:7b` | `qwen2.5:7b` | `mistral:7b` | Best speed/quality balance for most magazines. |
| `quality` | `llava:34b` (or best available) | `qwen2.5:14b` | `qwen2.5:14b` | `mistral:instruct` (larger) | Better accuracy, higher latency and memory use. |

The aliases `highquality`, `high-quality`, and `high_quality` are also accepted and map to `quality`.

The recommended mix keeps the expensive vision work concentrated in one stage and uses lighter text models elsewhere, which usually gives the best throughput without a major quality drop.

### Recommended system requirements by profile

| Profile | CPU | System RAM | GPU / VRAM | Storage | Expected Experience |
|---|---|---|---|---|---|
| `faster` | 6+ cores | 16 GB | 8 GB VRAM (or CPU-only, slower) | 20+ GB free | Good for quick runs and iterative tuning. |
| `balanced` | 8+ cores | 32 GB | 12–16 GB VRAM | 30+ GB free | Reliable quality with practical runtime for full issues. |
| `quality` | 12+ cores | 64 GB | 24+ GB VRAM | 50+ GB free | Best quality, but noticeably slower and heavier on resources. |

**Quick selection guide:** Start with `balanced`. Drop to `faster` if your GPU has less than 12 GB VRAM or if run time is too slow. Move to `quality` only if you have 24+ GB VRAM and output accuracy on `balanced` is noticeably poor.

Notes:

- These are practical recommendations, not strict minimums.
- Exact requirements depend on quantization level, context length, and concurrent model loading in Ollama.
- If VRAM is limited, use the `faster` profile or smaller quantized variants of the same models (e.g., `:q4_K_M`).

## Notes

- Ensure Ollama is running at `http://127.0.0.1:11434` or set `--ollama-endpoint`.
- Language data files are provided via the `tessdata` git submodule in the root of this repository. After cloning, run `git submodule update --init` to populate them.
- You can still set a global fallback with `--ollama-model` if desired.
