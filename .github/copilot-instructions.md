# Magazine Parser — Copilot Instructions

## Project Overview

`magparse` is a .NET 10 CLI tool that parses scanned magazine PDFs into structured JSON using PDF rendering (Docnet.Core), Tesseract OCR, and Ollama local model inference. The pipeline is fully sequential: render → save page images → OCR + page analysis → metadata inference → article grouping → article analysis → write output.

CLI input supports both a single PDF path and a directory path containing multiple PDF issues.

## Solution Layout

```
MagazineParser.slnx
magparse/                         ← main CLI project
  Program.cs                      ← entry point, argument parsing, JSON serialization
  Models/
    MagazineModels.cs             ← MagazineDocument, ArticleDocument, ArticleImage, enums
    InferenceModels.cs            ← PageInsight, MagazineMetadataCandidate, ArticleAnalysisCandidate, etc.
  Services/
    CliArgumentParser.cs          ← argument parsing, CliOptions, CliParseState, OutputPathResolver
    MagparseSettings.cs           ← settings resolution from CLI options + env vars, profile presets
    MagazineParserPipeline.cs     ← main pipeline: ParseAsync and all sub-stages
    OllamaInferenceClient.cs      ← Ollama HTTP wrapper with retry, timeout backoff, JSON repair
    PdfPageRenderer.cs            ← PDF → JPEG rendering via Docnet.Core
    ProgressReporting.cs          ← console progress reporter (plain and structured JSON)
    TesseractOcrClient.cs         ← OCR via Tesseract, falls back to empty string on failure
magparse.Tests/                   ← xUnit test project
  CliArgumentParserTests.cs
  OutputPathResolverTests.cs
tessdata/                         ← git submodule (tesseract-ocr/tessdata)
```

## Key Conventions

- **Target framework**: `net10.0`
- **Nullable reference types**: enabled — always use `string?` for optional strings, never `string` with a null check
- **Immutable models**: all model classes use `{ get; init; }` with `required` where the value is always present
- **No exceptions for control flow**: use `bool TryXxx(out ...)` patterns for fallible operations
- **Enums are camelCase in JSON**: `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` applied globally
- **No DateOnly in inference models**: `releaseDate` and all date fields are `string?` — models produce free-form date text

## Output Directory Structure

Every parse run writes into the output directory (`--output`; default: `./<pdf-name>/`):

```
<output-dir>/
  <pdf-name>.json       ← structured JSON (written by Program.cs after ParseAsync)
  ocr.txt               ← full OCR transcript (written by WriteOcrOutputAsync inside pipeline)
  pages/
    page-001.jpg        ← every rendered page (written by SavePageImagesAsync inside pipeline)
    page-002.jpg
    ...
```

Image paths in JSON (coverImagePath, logoImagePath, images[].filePath) are **relative** to the output directory.

Each article now carries both cleaned and raw OCR text (`text` and `rawText`).

## Input Modes and ML.NET Continuity Models

- **Single PDF input**: parse one issue. The runtime attempts to load an existing ML.NET continuity model for the issue key (derived from file name) to assist continuation/merge decisions.
- **Directory input**: parse all `*.pdf` files in the directory as issues of one title. A shared ML.NET continuity model is reused and updated during batch processing.
- **Existing outputs**: when the expected issue JSON already exists in the output directory, the issue is skipped automatically.
- **Force mode**: `--force` clears the output directory and reprocesses all targeted issues from scratch.
- **In-progress staging**: each issue is written under an `inprogress/` directory first and moved to its final output directory only after the issue completes successfully. Stale in-progress issue folders are cleared before retrying that issue.
- Model artefacts are stored under `./.magparse-models/<title-or-directory-key>/`.

## Pipeline Stages

| Stage | Method | Model setting |
|---|---|---|
| Render pages | `RenderPagesAsync` | — (Docnet.Core) |
| Save page images | `SavePageImagesAsync` | — |
| OCR + page analysis | `AnalyzePageAsync` (loop) | `OllamaPageAnalysisModel` (vision) |
| Write OCR text | `WriteOcrOutputAsync` | — |
| Metadata inference | `InferMagazineMetadataAsync` | `OllamaMetadataModel` |
| Article grouping | `RefineArticleDraftsAsync` | `OllamaGroupingModel` |
| Article analysis | `AnalyzeArticleAsync` (loop) | `OllamaArticleAnalysisModel` |

## Model Profiles

Profiles set all four model options at once. Defined in `MagparseSettings.ResolveProfileDefaults`.

| Profile key | Page analysis | Metadata | Grouping | Article analysis |
|---|---|---|---|---|
| `faster` | `llava:7b` | `qwen2.5:3b` | `qwen2.5:3b` | `qwen2.5:7b` |
| `balanced` | `llava:13b` | `qwen2.5:7b` | `qwen2.5:7b` | `mistral:7b` |
| `quality` | `llava:34b` | `qwen2.5:14b` | `qwen2.5:14b` | `mistral:instruct` |

`highquality`, `high-quality`, and `high_quality` are normalized to `quality` via `MagparseSettings.NormalizeProfileName`. Unknown profile names are rejected at CLI parse time.

## Ollama Inference

- All inference goes through `OllamaInferenceClient.GenerateJsonAsync<T>`
- On `JsonException`: retries up to `MaxGenerateAttempts` (3) with a stronger prompt, and attempts JSON repair via `TryRepairJsonPayloadAsync` (asks the model to fix malformed JSON) before giving up
- On timeout: retries up to `MaxTimeoutAttempts` (3) with exponential backoff on the timeout value
- All JSON is deserialized with `PropertyNameCaseInsensitive = true`, `AllowReadingFromString` for numbers, and a `LenientEnumJsonConverterFactory` that returns the default enum value for unrecognised strings

## Article Continuity and Title Recovery

Two mechanisms handle missing/vertical titles:

1. **Vision fallback** (`TryRecoverHeadingFromImageAsync`): triggered when `startsNewArticle=true`, `primaryHeading=null`, and pageKind is article or review. A second vision-only call asks specifically for headline text including vertical lettering.

2. **Split-title synthesis** (`ResolveDraftTitle`): collects heading fragments from the draft's first up to 2 pages. If all fragments are ≤8 words each, they are joined with a space.

## Testing

Run tests with:

```powershell
dotnet test .\MagazineParser.slnx
```

Tests are in `magparse.Tests/`. Current coverage: `CliArgumentParserTests` (argument parsing, profile normalization, unknown profile rejection) and `OutputPathResolverTests` (default directory naming, explicit path passthrough).

## Environment Variables

All CLI options have an environment variable fallback, resolved in `MagparseSettings.FromEnvironment`:

| Variable | Default |
|---|---|
| `MAGPARSE_OLLAMA_ENDPOINT` | `http://127.0.0.1:11434` |
| `MAGPARSE_OLLAMA_MODEL` | `gemma3:12b` |
| `MAGPARSE_OLLAMA_PAGE_ANALYSIS_MODEL` | (falls back to `MAGPARSE_OLLAMA_MODEL`) |
| `MAGPARSE_OLLAMA_METADATA_MODEL` | (falls back to `MAGPARSE_OLLAMA_MODEL`) |
| `MAGPARSE_OLLAMA_GROUPING_MODEL` | (falls back to `MAGPARSE_OLLAMA_MODEL`) |
| `MAGPARSE_OLLAMA_ARTICLE_ANALYSIS_MODEL` | (falls back to `MAGPARSE_OLLAMA_MODEL`) |
| `MAGPARSE_RENDER_SCALE` | `2.5` |
| `MAGPARSE_OLLAMA_TIMEOUT` | `900` (seconds) |
| `MAGPARSE_TESSDATA_PATH` | `./tessdata` |
| `MAGPARSE_TESSERACT_LANGUAGE` | `eng` |


