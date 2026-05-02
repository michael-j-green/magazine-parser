# magparse.OcrTraining

Train lightweight ML models for article type and article length from OCR-derived features.

This trainer supports both:
- direct PDF input (it renders and exports page images first)
- existing page-image directories

## What this trainer does

- Accepts either a PDF file path or a directory of `page-*.jpg` images.
- If input is a PDF, renders pages to JPEG and exports them.
- Runs OCR on page images with the same Tesseract client as parser.
- Builds tabular features and trains:
  - `article-type-model.zip` (multiclass)
  - `article-length-model.zip` (regression)
- Writes `training-summary.json`.

## Prerequisites

- .NET SDK 10
- `tessdata` folder available (default `./tessdata`)

## Labels JSON

Example: `./samples/training/labels.example.json`

```json
{
  "issueId": "CommodoreFormatHiiPdf000",
  "articles": [
    { "id": "a-001", "type": "editorial", "pages": [3] },
    { "id": "a-002", "type": "feature", "pages": [4] },
    { "id": "a-003", "type": "tips", "pages": [5] },
    { "id": "a-004", "type": "feature", "pages": [7, 8, 9] }
  ]
}
```

Notes:
- `type` values use parser enum camelCase values. Valid values are:
  `unknown`, `cover`, `editorial`, `news`, `feature`, `interview`, `review`, `preview`, `tips`, `guide`, `letters`, `advertisement`, `contents`, `other`
- Unlabeled pages are skipped.

## Build

```powershell
dotnet build ./magparse.OcrTraining/magparse.OcrTraining.csproj
```

## Usage

```powershell
dotnet run --project ./magparse.OcrTraining -- <pdf-or-images-path> <labels-json> [--output <dir>] [--export-pages-dir <dir>] [--render-scale <number>] [--max-pages <n>] [--tessdata-path <path>] [--tesseract-language <id>]
```

The model is **per title**, not per issue. Train from one or more issues of the same magazine and output to a directory named after the title. The parser resolves the model by the name of the directory containing the PDF (single-file mode) or the batch input directory name (directory mode), so both automatically pick up a model stored under `.magparse-models/<title-key>`.

### Train directly from a PDF (recommended)

```powershell
# Replace 'CommodoreFormat' with your magazine's title key
dotnet run --project ./magparse.OcrTraining -- ./samples/CommodoreFormat/issue-01.pdf ./samples/training/labels.json --output ./.magparse-models/CommodoreFormat --export-pages-dir ./output/training-pages --render-scale 2.5
```

### Train from an existing image directory

```powershell
dotnet run --project ./magparse.OcrTraining -- ./output/CommodoreFormat/issue-01/pages ./samples/training/labels.json --output ./.magparse-models/CommodoreFormat
```

### Adding more issues to an existing model

Run the trainer again with a different issue's PDF and the same `--output` directory. Each run overwrites the model files in place — to improve quality, label and train from as many representative issues as you have.

## Output artifacts

Models and summary are written to the `--output` directory you specify. Use the magazine title as the directory name so the parser picks them up automatically:

`./.magparse-models/<title-key>`

If `--output` is omitted, defaults to:

`./.magparse-models/ocr-trainer`

Files:
- `article-type-model.zip`
- `article-length-model.zip`
- `training-summary.json`

If input is a PDF:
- rendered page images are exported to `--export-pages-dir`
- if omitted, defaults to `<output>/rendered-pages`

## Metrics and minimum data

- Type metric: micro accuracy
- Length metric: R-squared

Minimums:
- Type model: at least 12 labeled pages
- Length model: at least 8 labeled articles

## Workflow

1. Label a representative sample of pages from one issue.
2. Run the trainer with `--output ./.magparse-models/<title-key>`.
3. Inspect `training-summary.json` — check micro accuracy and R-squared.
4. Add labels for weak or confused article classes.
5. Retrain, adding more issues over time to improve coverage.
6. Run the parser — it picks up the title-key model automatically.
7. Tune bypass thresholds with `--ocr-ml-type-confidence` / `--ocr-ml-length-confidence` as needed.

## Troubleshooting

### No page images found

- If input is a directory, ensure files match `page-*.jpg`.
- If input is a PDF, check `--render-scale` and permissions for export dir.

### Labels invalid

Ensure JSON is valid and `articles` has entries.

### OCR errors

Check `--tessdata-path` and `--tesseract-language`.
