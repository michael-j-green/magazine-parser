# Magazine Parser

Parse scanned magazine PDFs into structured JSON using:

- PDF page rendering
- Tesseract OCR
- Ollama model inference (per stage)

## Arguments

| Argument | Required | Default | Description |
|---|---|---|---|
| `<input-path>` | Yes | — | Path to a PDF file, or a directory containing multiple PDF issues of the same title. |
| `-o`, `--output <directory>` | | `./<pdf-name>/` in current directory | Output directory for all artefacts. |
| `--model-root <directory>` | | `./.magparse-models/` in current directory | Root directory for continuity and OCR ML models. Use this to ensure parser and trainer share the same model store. |
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
| `--force` | | `false` | Ignore existing output, clear the output directory, and reprocess from scratch. |
| `-h`, `--help` | | | Show usage information and exit. |

### Input modes

- Single PDF mode: process one issue directly. If a continuity model already exists for that issue key, it is loaded and used during continuation/merge decisions.
- Directory mode: process all `*.pdf` files in the directory as separate issues of one title. A shared ML.NET continuity model is created/updated for that directory key and reused while processing the batch.
- Existing outputs are skipped automatically when the expected issue JSON already exists in the output directory.
- Use `--force` to clear the output directory and reprocess everything from scratch.
- Each issue is written to an `inprogress` staging directory first and moved into its final output directory only after completion.
- Stale `inprogress` issue folders are cleared automatically before that issue is retried.

Stored model artefacts are written under `./.magparse-models/` by default, or under `--model-root` when provided.

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

  During processing, issue artefacts are staged under a sibling `inprogress/` directory and promoted to the final location only when the issue completes successfully.

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
      "text": "Full cleaned OCR body text...",
      "rawText": "Original Tesseract OCR text before cleanup..."
    }
  ]
}
```

### Enum values

| Enum | Values |
|---|---|
| `MagazineType` | `unknown`, `computing`, `gaming`, `technology`, `music`, `film`, `fashion`, `news`, `generalInterest`, `hobby`, `lifestyle`, `business`, `other` |
| `ArticleType` | `unknown`, `cover`, `editorial`, `news`, `feature`, `interview`, `review`, `preview`, `guide`, `letters`, `advertisement`, `contents`, `other` |
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

```powershell
# Batch mode: treat all PDFs in this directory as issues of one title
.\magparse\bin\Debug\net10.0\magparse.exe C:\path\to\commodore-format-issues `
  -o .\output\commodore-format `
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

## ML-First Article Routing

When a trained OCR model is available (see [magparse.OcrTraining](magparse.OcrTraining/README.md)), `magparse` uses it to predict each article's type and expected length directly from OCR features, skipping the LLM call for articles where the model is confident enough. This speeds up large batch runs without sacrificing quality on articles the model knows well.

### How bypass works

1. The OCR model predicts `type` confidence and `length` confidence for each article.
2. If **both** confidences meet their thresholds **and** the actual page count is within `--ocr-ml-max-page-delta` of the predicted length, the LLM analysis call is skipped.
3. Review and preview articles are **always** sent to the LLM regardless of thresholds.
4. If no trained model is found, all articles go through the LLM as normal.

### Threshold options

| Argument | Env Variable | Default | Description |
|---|---|---|---|
| `--ocr-ml-type-confidence <0-1>` | `MAGPARSE_OCR_ML_TYPE_CONFIDENCE` | `0.88` | Minimum model confidence on article type required to bypass the LLM. |
| `--ocr-ml-length-confidence <0-1>` | `MAGPARSE_OCR_ML_LENGTH_CONFIDENCE` | `0.75` | Minimum model confidence on article length required to bypass the LLM. |
| `--ocr-ml-max-page-delta <n>` | `MAGPARSE_OCR_ML_MAX_PAGE_DELTA` | `1` | Maximum allowed difference (in pages) between predicted and actual article length. |
| `--ocr-ml-type-override-confidence <0-1>` | `MAGPARSE_OCR_ML_TYPE_OVERRIDE_CONFIDENCE` | `0.92` | When the LLM **is** used, override its type assignment with the model's prediction if model confidence exceeds this value. |

### Recommended starting values

| Profile | `--ocr-ml-type-confidence` | `--ocr-ml-length-confidence` | `--ocr-ml-max-page-delta` | Notes |
|---|---|---|---|---|
| `faster` | `0.80` | `0.70` | `2` | Bypass more aggressively; accept slightly more prediction risk. |
| `balanced` | `0.88` *(default)* | `0.75` *(default)* | `1` *(default)* | Good balance between bypass rate and accuracy. |
| `quality` | `0.92` | `0.82` | `0` | Only bypass when the model is highly confident and page count matches exactly. |

To **disable ML bypass entirely** and always use the LLM for article analysis:

```powershell
.\magparse\bin\Debug\net10.0\magparse.exe C:\path\to\magazine.pdf --ocr-ml-type-confidence 1.0
```

## Notes

- Ensure Ollama is running at `http://127.0.0.1:11434` or set `--ollama-endpoint`.
- Language data files are provided via the `tessdata` git submodule in the root of this repository. After cloning, run `git submodule update --init` to populate them.
- You can still set a global fallback with `--ollama-model` if desired.


