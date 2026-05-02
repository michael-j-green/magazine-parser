using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using magparse.Models;
using magparse.Services;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace magparse.OcrTraining;

internal static class Program
{
    private static readonly JsonSerializerOptions LabelJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly Regex PageRegex = new("page-(\\d{1,4})\\.jpg$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArgs(args, out var options, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                await Console.Error.WriteLineAsync(error);
                await Console.Error.WriteLineAsync();
            }

            await PrintUsageAsync();
            return 1;
        }

        try
        {
            var labels = await LoadLabelsAsync(options.LabelsPath);
            var pageToArticleType = BuildPageTypeMap(labels);
            var pageToArticleId = BuildPageArticleIdMap(labels);

            var imageFiles = await ResolveImageFilesAsync(options);

            if (imageFiles.Length == 0)
            {
                await Console.Error.WriteLineAsync($"No page images found for input: {options.InputPath}");
                return 1;
            }

            var settings = BuildOcrSettings(options);
            var ocrClient = new TesseractOcrClient(settings);

            var pageFeatureRows = new List<PageTypeTrainingRow>();
            var articleBuckets = new Dictionary<string, List<PageFeatures>>(StringComparer.OrdinalIgnoreCase);
            var skippedPages = 0;

            foreach (var imageFile in imageFiles)
            {
                var pageNumber = TryGetPageNumber(imageFile);
                if (!pageNumber.HasValue)
                {
                    continue;
                }

                if (!pageToArticleType.TryGetValue(pageNumber.Value, out var articleType) || articleType == ArticleType.Unknown)
                {
                    skippedPages++;
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(imageFile);
                var renderedPage = new RenderedPage
                {
                    PageNumber = pageNumber.Value,
                    Width = 0,
                    Height = 0,
                    JpegBytes = bytes
                };

                var ocr = await ocrClient.ReadTextWithDiagnosticsAsync(renderedPage, CancellationToken.None);
                var pageFeatures = PageFeatures.From(ocr.Text, ocr.Confidence, ocr.Score);

                pageFeatureRows.Add(new PageTypeTrainingRow
                {
                    Label = articleType.ToString(),
                    WordCount = pageFeatures.WordCount,
                    AverageWordLength = pageFeatures.AverageWordLength,
                    UppercaseRatio = pageFeatures.UppercaseRatio,
                    DigitRatio = pageFeatures.DigitRatio,
                    PunctuationRatio = pageFeatures.PunctuationRatio,
                    NewLineCount = pageFeatures.NewLineCount,
                    OcrConfidence = pageFeatures.OcrConfidence,
                    OcrScore = pageFeatures.OcrScore,
                    TipsKeywordHits = pageFeatures.TipsKeywordHits,
                    ReviewKeywordHits = pageFeatures.ReviewKeywordHits
                });

                if (pageToArticleId.TryGetValue(pageNumber.Value, out var articleId))
                {
                    if (!articleBuckets.TryGetValue(articleId, out var bucket))
                    {
                        bucket = [];
                        articleBuckets[articleId] = bucket;
                    }

                    bucket.Add(pageFeatures);
                }
            }

            Directory.CreateDirectory(options.OutputDirectory);

            var typeModelPath = Path.Combine(options.OutputDirectory, "article-type-model.zip");
            var lengthModelPath = Path.Combine(options.OutputDirectory, "article-length-model.zip");

            var typeTrainingSummary = TrainArticleTypeModel(pageFeatureRows, typeModelPath);
            var lengthTrainingRows = BuildLengthRows(labels, articleBuckets);
            var lengthTrainingSummary = TrainArticleLengthModel(lengthTrainingRows, lengthModelPath);

            await WriteSummaryAsync(
                options.OutputDirectory,
                labels,
                imageFiles.Length,
                skippedPages,
                pageFeatureRows.Count,
                lengthTrainingRows.Count,
                typeTrainingSummary,
                lengthTrainingSummary);

            await Console.Out.WriteLineAsync("Training complete.");
            await Console.Out.WriteLineAsync($"Type model: {typeModelPath}");
            await Console.Out.WriteLineAsync($"Length model: {lengthModelPath}");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static MagparseSettings BuildOcrSettings(TrainerOptions options)
    {
        return new MagparseSettings
        {
            OllamaEndpoint = new Uri("http://127.0.0.1:11434"),
            OllamaModel = "unused",
            OllamaPageAnalysisModel = "unused",
            OllamaMetadataModel = "unused",
            OllamaGroupingModel = "unused",
            OllamaArticleAnalysisModel = "unused",
            RenderScale = options.RenderScale,
            OllamaTimeoutSeconds = 30,
            TessDataPath = options.TessDataPath,
            TesseractLanguage = options.TesseractLanguage,
            OcrMlTypeConfidenceThreshold = 0.88,
            OcrMlLengthConfidenceThreshold = 0.75,
            OcrMlMaxPageDeltaThreshold = 1,
            OcrMlTypeOverrideConfidenceThreshold = 0.92
        };
    }

    private static async Task<string[]> ResolveImageFilesAsync(TrainerOptions options)
    {
        if (Directory.Exists(options.InputPath))
        {
            return Directory
                .EnumerateFiles(options.InputPath, "page-*.jpg", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (!File.Exists(options.InputPath) || !string.Equals(Path.GetExtension(options.InputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var settings = BuildOcrSettings(options);
        var renderer = new PdfPageRenderer(settings);
        var progress = new ConsoleProgressReporter(emitStructuredProgress: false);
        var exportDirectory = string.IsNullOrWhiteSpace(options.ExportPagesDirectory)
            ? Path.Combine(options.OutputDirectory, "rendered-pages")
            : Path.GetFullPath(options.ExportPagesDirectory);

        Directory.CreateDirectory(exportDirectory);

        var renderedPages = await renderer.RenderAllPagesAsync(
            new ParseRequest
            {
                PdfPath = options.InputPath,
                MaxPages = options.MaxPages,
                SummaryOnly = true,
                OutputDirectory = exportDirectory
            },
            progress,
            CancellationToken.None);

        foreach (var page in renderedPages)
        {
            var path = Path.Combine(exportDirectory, $"page-{page.PageNumber:D3}.jpg");
            await File.WriteAllBytesAsync(path, page.JpegBytes);
        }

        await Console.Out.WriteLineAsync($"Rendered {renderedPages.Count} page(s) from PDF into {exportDirectory}");

        return Directory
            .EnumerateFiles(exportDirectory, "page-*.jpg", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<TrainingLabels> LoadLabelsAsync(string labelsPath)
    {
        var json = await File.ReadAllTextAsync(labelsPath);
        var labels = JsonSerializer.Deserialize<TrainingLabels>(json, LabelJsonOptions);
        if (labels is null || labels.Articles.Count == 0)
        {
            throw new InvalidOperationException("Labels file is empty or invalid.");
        }

        return labels;
    }

    private static Dictionary<int, ArticleType> BuildPageTypeMap(TrainingLabels labels)
    {
        var map = new Dictionary<int, ArticleType>();
        foreach (var article in labels.Articles)
        {
            foreach (var page in article.Pages)
            {
                map[page] = article.Type;
            }
        }

        return map;
    }

    private static Dictionary<int, string> BuildPageArticleIdMap(TrainingLabels labels)
    {
        var map = new Dictionary<int, string>();
        foreach (var article in labels.Articles)
        {
            var id = string.IsNullOrWhiteSpace(article.Id)
                ? $"{article.Type}-{article.Pages.Min()}-{article.Pages.Max()}"
                : article.Id;

            foreach (var page in article.Pages)
            {
                map[page] = id;
            }
        }

        return map;
    }

    private static int? TryGetPageNumber(string imagePath)
    {
        var match = PageRegex.Match(Path.GetFileName(imagePath));
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static ModelTrainingSummary TrainArticleTypeModel(IReadOnlyList<PageTypeTrainingRow> rows, string modelPath)
    {
        if (rows.Count < 12)
        {
            return new ModelTrainingSummary(false, rows.Count, null, "Not enough labeled pages to train type model (need at least 12).");
        }

        var ml = new MLContext(seed: 31);
        var data = ml.Data.LoadFromEnumerable(rows);

        var split = ml.Data.TrainTestSplit(data, testFraction: 0.2);

        var pipeline = ml.Transforms.Conversion.MapValueToKey(nameof(PageTypeTrainingRow.Label))
            .Append(ml.Transforms.Concatenate(
                "Features",
                nameof(PageTypeTrainingRow.WordCount),
                nameof(PageTypeTrainingRow.AverageWordLength),
                nameof(PageTypeTrainingRow.UppercaseRatio),
                nameof(PageTypeTrainingRow.DigitRatio),
                nameof(PageTypeTrainingRow.PunctuationRatio),
                nameof(PageTypeTrainingRow.NewLineCount),
                nameof(PageTypeTrainingRow.OcrConfidence),
                nameof(PageTypeTrainingRow.OcrScore),
                nameof(PageTypeTrainingRow.TipsKeywordHits),
                nameof(PageTypeTrainingRow.ReviewKeywordHits)))
            .Append(ml.MulticlassClassification.Trainers.SdcaMaximumEntropy())
            .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        var model = pipeline.Fit(split.TrainSet);

        var metrics = ml.MulticlassClassification.Evaluate(model.Transform(split.TestSet));

        using var fs = File.Create(modelPath);
        ml.Model.Save(model, split.TrainSet.Schema, fs);

        return new ModelTrainingSummary(true, rows.Count, metrics.MicroAccuracy, null);
    }

    private static List<ArticleLengthTrainingRow> BuildLengthRows(
        TrainingLabels labels,
        IReadOnlyDictionary<string, List<PageFeatures>> articleBuckets)
    {
        var rows = new List<ArticleLengthTrainingRow>();

        foreach (var article in labels.Articles)
        {
            var id = string.IsNullOrWhiteSpace(article.Id)
                ? $"{article.Type}-{article.Pages.Min()}-{article.Pages.Max()}"
                : article.Id;

            if (!articleBuckets.TryGetValue(id, out var pages) || pages.Count == 0)
            {
                continue;
            }

            rows.Add(new ArticleLengthTrainingRow
            {
                Type = article.Type.ToString(),
                PageCount = article.Pages.Count,
                MeanPageWordCount = (float)pages.Average(p => p.WordCount),
                MeanPageScore = (float)pages.Average(p => p.OcrScore),
                MeanTipsKeywordHits = (float)pages.Average(p => p.TipsKeywordHits),
                MeanReviewKeywordHits = (float)pages.Average(p => p.ReviewKeywordHits),
                Label = article.Pages.Count
            });
        }

        return rows;
    }

    private static ModelTrainingSummary TrainArticleLengthModel(IReadOnlyList<ArticleLengthTrainingRow> rows, string modelPath)
    {
        if (rows.Count < 8)
        {
            return new ModelTrainingSummary(false, rows.Count, null, "Not enough labeled articles to train length model (need at least 8).");
        }

        var ml = new MLContext(seed: 41);
        var data = ml.Data.LoadFromEnumerable(rows);
        var split = ml.Data.TrainTestSplit(data, testFraction: 0.2);

        var pipeline = ml.Transforms.Categorical.OneHotEncoding("TypeEncoded", nameof(ArticleLengthTrainingRow.Type))
            .Append(ml.Transforms.Concatenate(
                "Features",
                "TypeEncoded",
                nameof(ArticleLengthTrainingRow.PageCount),
                nameof(ArticleLengthTrainingRow.MeanPageWordCount),
                nameof(ArticleLengthTrainingRow.MeanPageScore),
                nameof(ArticleLengthTrainingRow.MeanTipsKeywordHits),
                nameof(ArticleLengthTrainingRow.MeanReviewKeywordHits)))
            .Append(ml.Regression.Trainers.Sdca())
            .Append(ml.Transforms.CopyColumns("Score", "Score"));

        var model = pipeline.Fit(split.TrainSet);
        var metrics = ml.Regression.Evaluate(model.Transform(split.TestSet));

        using var fs = File.Create(modelPath);
        ml.Model.Save(model, split.TrainSet.Schema, fs);

        if (!double.IsFinite(metrics.RSquared))
        {
            return new ModelTrainingSummary(
                true,
                rows.Count,
                null,
                "R-squared was non-finite for this train/test split. This commonly happens with very small or low-variance label sets; the model was still trained and saved.");
        }

        return new ModelTrainingSummary(true, rows.Count, metrics.RSquared, null);
    }

    private static async Task WriteSummaryAsync(
        string outputDirectory,
        TrainingLabels labels,
        int totalImages,
        int skippedPages,
        int usedPages,
        int usedArticles,
        ModelTrainingSummary typeSummary,
        ModelTrainingSummary lengthSummary)
    {
        typeSummary = SanitizeSummaryMetric(typeSummary, "type model metric");
        lengthSummary = SanitizeSummaryMetric(lengthSummary, "length model metric");

        var summary = new TrainingSummary
        {
            IssueId = labels.IssueId,
            TotalImages = totalImages,
            LabeledArticles = labels.Articles.Count,
            SkippedUnlabeledPages = skippedPages,
            UsedPagesForTypeModel = usedPages,
            UsedArticlesForLengthModel = usedArticles,
            TypeModel = typeSummary,
            LengthModel = lengthSummary
        };

        var path = Path.Combine(outputDirectory, "training-summary.json");
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(path, json);
    }

    private static ModelTrainingSummary SanitizeSummaryMetric(ModelTrainingSummary summary, string metricLabel)
    {
        if (summary.Metric is not double metric || double.IsFinite(metric))
        {
            return summary;
        }

        return summary with
        {
            Metric = null,
            Message = AppendMessage(summary.Message, $"{metricLabel} was non-finite and has been written as null in training-summary.json.")
        };
    }

    private static string AppendMessage(string? existing, string extra)
    {
        return string.IsNullOrWhiteSpace(existing)
            ? extra
            : existing + " " + extra;
    }

    private static bool TryParseArgs(string[] args, out TrainerOptions options, out string? error)
    {
        options = new TrainerOptions();
        error = null;

        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            error = null;
            return false;
        }

        if (args.Length < 2)
        {
            error = "You must provide <pdf-or-images-path> and <labels-json>.";
            return false;
        }

        options.InputPath = Path.GetFullPath(args[0]);
        options.LabelsPath = Path.GetFullPath(args[1]);
        options.OutputDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".magparse-models", "ocr-trainer"));
        options.TessDataPath = "./tessdata";
        options.TesseractLanguage = "eng";
        options.RenderScale = 2.5;

        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--output":
                    if (!TryReadValue(args, ref i, out var outputPath, out error))
                    {
                        return false;
                    }

                    options.OutputDirectory = Path.GetFullPath(outputPath);
                    break;
                case "--tessdata-path":
                    if (!TryReadValue(args, ref i, out var tessPath, out error))
                    {
                        return false;
                    }

                    options.TessDataPath = tessPath;
                    break;
                case "--tesseract-language":
                    if (!TryReadValue(args, ref i, out var lang, out error))
                    {
                        return false;
                    }

                    options.TesseractLanguage = lang;
                    break;
                case "--export-pages-dir":
                    if (!TryReadValue(args, ref i, out var exportPagesDir, out error))
                    {
                        return false;
                    }

                    options.ExportPagesDirectory = Path.GetFullPath(exportPagesDir);
                    break;
                case "--render-scale":
                    if (!TryReadValue(args, ref i, out var renderScaleRaw, out error))
                    {
                        return false;
                    }

                    if (!double.TryParse(renderScaleRaw, out var renderScale) || renderScale <= 0)
                    {
                        error = "--render-scale must be a positive number.";
                        return false;
                    }

                    options.RenderScale = renderScale;
                    break;
                case "--max-pages":
                    if (!TryReadValue(args, ref i, out var maxPagesRaw, out error))
                    {
                        return false;
                    }

                    if (!int.TryParse(maxPagesRaw, out var maxPages) || maxPages <= 0)
                    {
                        error = "--max-pages must be a positive integer.";
                        return false;
                    }

                    options.MaxPages = maxPages;
                    break;
                default:
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        if (!Directory.Exists(options.InputPath) && !File.Exists(options.InputPath))
        {
            error = $"Input path not found: {options.InputPath}";
            return false;
        }

        if (File.Exists(options.InputPath) && !string.Equals(Path.GetExtension(options.InputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            error = "When file input is used, it must be a .pdf.";
            return false;
        }

        if (!File.Exists(options.LabelsPath))
        {
            error = $"Labels file not found: {options.LabelsPath}";
            return false;
        }

        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value, out string? error)
    {
        value = string.Empty;
        error = null;

        if (index + 1 >= args.Length)
        {
            error = $"Missing value for {args[index]}";
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static async Task PrintUsageAsync()
    {
        await Console.Error.WriteLineAsync("Usage: magparse-ocr-train <pdf-or-images-path> <labels-json> [--output <dir>] [--export-pages-dir <dir>] [--render-scale <number>] [--max-pages <n>] [--tessdata-path <path>] [--tesseract-language <id>]");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Example:");
        await Console.Error.WriteLineAsync("  dotnet run --project ./magparse.OcrTraining -- ./samples/issue.pdf ./samples/training/labels.example.json --output ./.magparse-models/training/commodore-000 --export-pages-dir ./output/training-pages --render-scale 2.5");
    }

    private sealed class TrainerOptions
    {
        public string InputPath { get; set; } = string.Empty;

        public string LabelsPath { get; set; } = string.Empty;

        public string OutputDirectory { get; set; } = string.Empty;

        public string TessDataPath { get; set; } = string.Empty;

        public string TesseractLanguage { get; set; } = string.Empty;

        public string? ExportPagesDirectory { get; set; }

        public double RenderScale { get; set; }

        public int? MaxPages { get; set; }
    }

    private sealed class TrainingLabels
    {
        public string? IssueId { get; init; }

        public List<ArticleLabel> Articles { get; init; } = [];
    }

    private sealed class ArticleLabel
    {
        public string? Id { get; init; }

        public ArticleType Type { get; init; }

        public List<int> Pages { get; init; } = [];
    }

    private sealed class PageTypeTrainingRow
    {
        public string Label { get; init; } = string.Empty;

        public float WordCount { get; init; }

        public float AverageWordLength { get; init; }

        public float UppercaseRatio { get; init; }

        public float DigitRatio { get; init; }

        public float PunctuationRatio { get; init; }

        public float NewLineCount { get; init; }

        public float OcrConfidence { get; init; }

        public float OcrScore { get; init; }

        public float TipsKeywordHits { get; init; }

        public float ReviewKeywordHits { get; init; }
    }

    private sealed class ArticleLengthTrainingRow
    {
        public string Type { get; init; } = string.Empty;

        public float PageCount { get; init; }

        public float MeanPageWordCount { get; init; }

        public float MeanPageScore { get; init; }

        public float MeanTipsKeywordHits { get; init; }

        public float MeanReviewKeywordHits { get; init; }

        [ColumnName("Label")]
        public float Label { get; init; }
    }

    private sealed class PageFeatures
    {
        public float WordCount { get; init; }

        public float AverageWordLength { get; init; }

        public float UppercaseRatio { get; init; }

        public float DigitRatio { get; init; }

        public float PunctuationRatio { get; init; }

        public float NewLineCount { get; init; }

        public float OcrConfidence { get; init; }

        public float OcrScore { get; init; }

        public float TipsKeywordHits { get; init; }

        public float ReviewKeywordHits { get; init; }

        public static PageFeatures From(string text, double confidence, double score)
        {
            var words = text
                .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Any(char.IsLetterOrDigit))
                .ToArray();

            var totalChars = Math.Max(1, text.Length);
            var uppercase = text.Count(char.IsUpper);
            var digits = text.Count(char.IsDigit);
            var punctuation = text.Count(ch => ".,;:!?()[]{}<>-/%'\"".Contains(ch));
            var newLines = text.Count(ch => ch == '\n');

            var tipsKeywords = CountKeywordHits(text, [
                "tip",
                "tips",
                "walkthrough",
                "cheat",
                "level",
                "strategy",
                "hint"
            ]);

            var reviewKeywords = CountKeywordHits(text, [
                "review",
                "rating",
                "score",
                "graphics",
                "sound",
                "playability",
                "value"
            ]);

            var averageWordLength = words.Length == 0
                ? 0
                : (float)words.Average(word => word.Length);

            return new PageFeatures
            {
                WordCount = words.Length,
                AverageWordLength = averageWordLength,
                UppercaseRatio = (float)uppercase / totalChars,
                DigitRatio = (float)digits / totalChars,
                PunctuationRatio = (float)punctuation / totalChars,
                NewLineCount = newLines,
                OcrConfidence = (float)confidence,
                OcrScore = (float)score,
                TipsKeywordHits = tipsKeywords,
                ReviewKeywordHits = reviewKeywords
            };
        }

        private static float CountKeywordHits(string text, IReadOnlyList<string> keywords)
        {
            var lower = text.ToLowerInvariant();
            var count = 0;
            foreach (var keyword in keywords)
            {
                var idx = 0;
                while (true)
                {
                    idx = lower.IndexOf(keyword, idx, StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        break;
                    }

                    count++;
                    idx += keyword.Length;
                }
            }

            return count;
        }
    }

    private sealed class TrainingSummary
    {
        public string? IssueId { get; init; }

        public int TotalImages { get; init; }

        public int LabeledArticles { get; init; }

        public int SkippedUnlabeledPages { get; init; }

        public int UsedPagesForTypeModel { get; init; }

        public int UsedArticlesForLengthModel { get; init; }

        public ModelTrainingSummary TypeModel { get; init; } = new(false, 0, null, null);

        public ModelTrainingSummary LengthModel { get; init; } = new(false, 0, null, null);
    }

    private sealed record ModelTrainingSummary(bool Trained, int RowCount, double? Metric, string? Message);
}
