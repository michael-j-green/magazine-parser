# Magazine Parser

Parse scanned magazine PDFs into structured JSON using:

- PDF page rendering
- Tesseract OCR
- Ollama model inference (per stage)

## Arguments

| Argument | Required | Default | Description |
|---|---|---|---|
| `<pdf-path>` | Yes | — | Path to the PDF file to parse. |
| `-o`, `--output <path>` | | `<pdf-name>.json` in current directory | Output JSON file path or directory. |
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
| `--raw-output` | | `false` | Write raw OCR text alongside the JSON output. |
| `--progress` | | `false` | Emit machine-readable JSON progress events to stdout. |
| `-h`, `--help` | | | Show usage information and exit. |

## Recommended Model Command

This sample uses a balanced model setup:

| Stage | Recommended Model | Why |
|---|---|---|
| Page analysis (vision) | `llava:13b` | Strong at image-grounded layout understanding and page classification. |
| Metadata extraction | `qwen2.5:7b` | Fast and consistent for short structured outputs. |
| Article grouping | `qwen2.5:7b` | Good reasoning quality with lower latency for boundary decisions. |
| Article analysis | `mistral:7b` | Solid long-form summarization and interpretation quality. |

```powershell
.\magparse\bin\Debug\net10.0\magparse.exe C:\path\to\magazine.pdf `
  --raw-output `
  -o .\output\output.json `
  --profile balanced
```

You can override any individual stage by adding the corresponding flag alongside `--profile`:

```powershell
# Use the balanced profile but swap in a larger model for article analysis
.\magparse\bin\Debug\net10.0\magparse.exe C:\path\to\magazine.pdf `
  --profile balanced `
  --ollama-article-analysis-model mistral:instruct
```

### Why These Models

| Profile | Page Analysis (Vision) | Metadata | Grouping | Article Analysis | Tradeoff |
|---|---|---|---|---|---|
| Faster / Lower GPU | `llava:7b` | `qwen2.5:3b` | `qwen2.5:3b` | `qwen2.5:7b` | Fastest runtime, lower accuracy on harder pages. |
| Balanced (recommended) | `llava:13b` | `qwen2.5:7b` | `qwen2.5:7b` | `mistral:7b` | Best speed/quality balance for most magazines. |
| Higher Quality / Slower | `llava:34b` (or best vision model available) | `qwen2.5:14b` | `qwen2.5:14b` | `mistral:instruct` (larger variant) | Better extraction and analysis quality with higher latency and memory use. |

The recommended mix keeps the expensive vision work concentrated in one stage and uses lighter text models elsewhere, which usually gives the best throughput without a major quality drop.

### Recommended System Requirements By Profile

| Profile | CPU | System RAM | GPU / VRAM | Storage | Expected Experience |
|---|---|---|---|---|---|
| Faster / Lower GPU | 6+ cores | 16 GB | 8 GB VRAM (or CPU-only, slower) | 20+ GB free | Good for quick runs and iterative tuning. |
| Balanced (recommended) | 8+ cores | 32 GB | 12-16 GB VRAM | 30+ GB free | Reliable quality with practical runtime for full issues. |
| Higher Quality / Slower | 12+ cores | 64 GB | 24+ GB VRAM | 50+ GB free | Best quality, but noticeably slower and heavier on resources. |

**Quick selection guide:** Start with Balanced. Drop to Faster if your GPU has less than 12 GB VRAM or if run time is too slow. Move to Higher Quality only if you have 24+ GB VRAM and output accuracy on the Balanced profile is noticeably poor.

Notes:

- These are practical recommendations, not strict minimums.
- Exact requirements depend on quantization level, context length, and concurrent model loading in Ollama.
- If VRAM is limited, use the Faster profile or smaller quantized variants of the same models (e.g., `:q4_K_M`).

## Notes

- Ensure Ollama is running at `http://127.0.0.1:11434` or set `--ollama-endpoint`.
- Language data files are provided via the `tessdata` git submodule in the root of this repository. After cloning, run `git submodule update --init` to populate them.
- You can still set a global fallback with `--ollama-model` if desired.
