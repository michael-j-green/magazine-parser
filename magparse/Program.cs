using System.Text.Json;
using System.Text.Json.Serialization;
using magparse.Models;
using magparse.Services;

namespace magparse;

internal static class Program
{
    private const string DefaultsToModelEnvironmentVariableText = "                              Defaults to MAGPARSE_OLLAMA_MODEL";

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> Main(string[] args)
    {
        if (!CliArgumentParser.TryParse(args, out var options, out var errorMessage))
        {
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                await Console.Error.WriteLineAsync(errorMessage);
                await Console.Error.WriteLineAsync();
            }

            await PrintUsageAsync();
            return 1;
        }

        if (options.ShowHelp)
        {
            await PrintUsageAsync();
            return 0;
        }

        var inputPath = Path.GetFullPath(options.InputPath);
        var inputIsFile = File.Exists(inputPath);
        var inputIsDirectory = Directory.Exists(inputPath);

        if (!inputIsFile && !inputIsDirectory)
        {
            await Console.Error.WriteLineAsync($"The input path was not found: {inputPath}");
            return 1;
        }

        try
        {
            var settings = MagparseSettings.FromEnvironment(options);
            var progressReporter = new ConsoleProgressReporter(options.EmitStructuredProgress);
            var renderer = new PdfPageRenderer(settings);
            var ollama = new OllamaInferenceClient(settings);
            var ocr = new TesseractOcrClient(settings);
            var modelRootDirectory = string.IsNullOrWhiteSpace(options.ModelRootPath)
                ? Path.Combine(Environment.CurrentDirectory, ".magparse-models")
                : Path.GetFullPath(options.ModelRootPath);

            return inputIsDirectory
                ? await ParseDirectoryAsync(options, inputPath, modelRootDirectory, settings, renderer, ollama, ocr, progressReporter)
                : await ParseSinglePdfAsync(options, inputPath, modelRootDirectory, settings, renderer, ollama, ocr, progressReporter);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task<int> ParseSinglePdfAsync(
        CliOptions options,
        string pdfPath,
        string modelRootDirectory,
        MagparseSettings settings,
        PdfPageRenderer renderer,
        OllamaInferenceClient ollama,
        TesseractOcrClient ocr,
        IProgressReporter progressReporter)
    {
        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync("When a file path is supplied, it must be a PDF.");
            return 1;
        }

        var outputDirectory = OutputPathResolver.Resolve(options.OutputPath, pdfPath, Environment.CurrentDirectory);
        PurgeInProgressRoot(outputDirectory);
        if (options.Force)
        {
            ValidateForceOutputDirectory(outputDirectory, Path.GetDirectoryName(pdfPath), "single-file");
            ClearOutputDirectory(outputDirectory);
        }

        if (!options.Force && IsIssueAlreadyProcessed(outputDirectory, pdfPath))
        {
            await progressReporter.ReportAsync(
                "skipped",
                $"Skipping {Path.GetFileName(pdfPath)} because output already exists in {outputDirectory}",
                current: 1,
                total: 1,
                percent: 1);
            return 0;
        }

        var inProgressDirectory = GetInProgressDirectory(outputDirectory);
        ClearOutputDirectory(inProgressDirectory);

        var singleIssueTitleKey = Path.GetFileName(
            Path.GetFullPath(Path.GetDirectoryName(pdfPath) ?? Environment.CurrentDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var continuityModelDirectory = ContinuityModelPathResolver.ResolveModelDirectory(modelRootDirectory, Path.GetFileNameWithoutExtension(pdfPath));
        var ocrModelDirectory = OcrArticleModelPathResolver.ResolveModelDirectory(modelRootDirectory, singleIssueTitleKey);
        var predictor = new MlNetArticleContinuityPredictor(
            continuityModelDirectory);
        var ocrArticlePredictor = new OcrArticleModelPredictor(
            ocrModelDirectory,
            settings);

        await progressReporter.ReportAsync(
            "startup",
            $"Starting parse for {Path.GetFileName(pdfPath)}");
        await progressReporter.ReportAsync(
            "startup",
            $"Model root directory: {Path.GetFullPath(modelRootDirectory)}");
        await progressReporter.ReportAsync(
            "startup",
            DescribeContinuityModelSelection(continuityModelDirectory));
        await progressReporter.ReportAsync(
            "startup",
            DescribeOcrModelSelection(modelRootDirectory, ocrModelDirectory));

        var parser = new MagazineParserPipeline(settings, renderer, ollama, ocr, progressReporter, predictor, ocrArticlePredictor);
        var magazine = await parser.ParseAsync(new ParseRequest
        {
            PdfPath = pdfPath,
            MaxPages = options.MaxPages,
            SummaryOnly = options.SummaryOnly,
            OutputDirectory = inProgressDirectory
        }, CancellationToken.None);

        Directory.CreateDirectory(inProgressDirectory);
        var jsonPath = Path.Combine(inProgressDirectory, Path.GetFileNameWithoutExtension(pdfPath) + ".json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(magazine, OutputJsonOptions));
        PromoteCompletedIssueOutput(inProgressDirectory, outputDirectory);

        var finalJsonPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(pdfPath) + ".json");
        await PostProcessMergeAsync([finalJsonPath], progressReporter);

        await progressReporter.ReportAsync(
            "complete",
            $"Finished parsing {magazine.PageCount} page(s) into {magazine.Articles.Count} article(s). Output written to {outputDirectory}",
            current: 1,
            total: 1,
            percent: 1);

        return 0;
    }

    private static async Task<int> ParseDirectoryAsync(
        CliOptions options,
        string directoryPath,
        string modelRootDirectory,
        MagparseSettings settings,
        PdfPageRenderer renderer,
        OllamaInferenceClient ollama,
        TesseractOcrClient ocr,
        IProgressReporter progressReporter)
    {
        var pdfPaths = Directory
            .EnumerateFiles(directoryPath, "*.pdf", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (pdfPaths.Length == 0)
        {
            await Console.Error.WriteLineAsync($"No PDF files were found in directory: {directoryPath}");
            return 1;
        }

        var directoryKey = Path.GetFileName(Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var continuityModelDirectory = ContinuityModelPathResolver.ResolveModelDirectory(modelRootDirectory, directoryKey);
        var ocrModelDirectory = OcrArticleModelPathResolver.ResolveModelDirectory(modelRootDirectory, directoryKey);
        var predictor = new MlNetArticleContinuityPredictor(
            continuityModelDirectory);
        var ocrArticlePredictor = new OcrArticleModelPredictor(
            ocrModelDirectory,
            settings);
        var parser = new MagazineParserPipeline(settings, renderer, ollama, ocr, progressReporter, predictor, ocrArticlePredictor);

        var outputRoot = string.IsNullOrWhiteSpace(options.OutputPath)
            ? Path.Combine(Environment.CurrentDirectory, directoryKey)
            : Path.GetFullPath(options.OutputPath);

        PurgeInProgressRoot(outputRoot);

        if (options.Force)
        {
            ValidateForceOutputDirectory(outputRoot, directoryPath, "directory-batch");
            ClearOutputDirectory(outputRoot);
        }

        await progressReporter.ReportAsync(
            "startup",
            $"Starting batch parse for {pdfPaths.Length} PDF file(s) in {directoryPath}");
        await progressReporter.ReportAsync(
            "startup",
            $"Model root directory: {Path.GetFullPath(modelRootDirectory)}");
        await progressReporter.ReportAsync(
            "startup",
            DescribeContinuityModelSelection(continuityModelDirectory));
        await progressReporter.ReportAsync(
            "startup",
            DescribeOcrModelSelection(modelRootDirectory, ocrModelDirectory));

        var failedIssues = new List<string>();
        var skippedIssues = new List<string>();

        for (var index = 0; index < pdfPaths.Length; index++)
        {
            var pdfPath = pdfPaths[index];
            var issueOutputDirectory = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(pdfPath));
            var issueInProgressDirectory = GetInProgressDirectory(issueOutputDirectory);

            await progressReporter.ReportAsync(
                "batch-item",
                $"Processing issue {index + 1}/{pdfPaths.Length}: {Path.GetFileName(pdfPath)}",
                current: index + 1,
                total: pdfPaths.Length);

            if (!options.Force && IsIssueAlreadyProcessed(issueOutputDirectory, pdfPath))
            {
                skippedIssues.Add(Path.GetFileName(pdfPath));
                await progressReporter.ReportAsync(
                    "batch-item-skipped",
                    $"Skipping issue {Path.GetFileName(pdfPath)} because output already exists.",
                    current: index + 1,
                    total: pdfPaths.Length);
                continue;
            }

            ClearOutputDirectory(issueInProgressDirectory);

            try
            {
                var magazine = await parser.ParseAsync(new ParseRequest
                {
                    PdfPath = pdfPath,
                    MaxPages = options.MaxPages,
                    SummaryOnly = options.SummaryOnly,
                    OutputDirectory = issueInProgressDirectory
                }, CancellationToken.None);

                Directory.CreateDirectory(issueInProgressDirectory);
                var jsonPath = Path.Combine(issueInProgressDirectory, Path.GetFileNameWithoutExtension(pdfPath) + ".json");
                await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(magazine, OutputJsonOptions));
                PromoteCompletedIssueOutput(issueInProgressDirectory, issueOutputDirectory);
            }
            catch (Exception exception)
            {
                failedIssues.Add(Path.GetFileName(pdfPath));
                await Console.Error.WriteLineAsync($"Failed issue {Path.GetFileName(pdfPath)}: {exception.Message}");
                await progressReporter.ReportAsync(
                    "batch-item-error",
                    $"Issue {Path.GetFileName(pdfPath)} failed and was skipped.",
                    current: index + 1,
                    total: pdfPaths.Length);
            }
        }

        var successCount = pdfPaths.Length - failedIssues.Count - skippedIssues.Count;

        // Re-evaluate article boundaries across all completed issues now that the ML model
        // has been trained on the full batch. Early issues may have been split incorrectly
        // when the model was still undertrained.
        var allJsonPaths = pdfPaths
            .Select(p => Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(p), Path.GetFileNameWithoutExtension(p) + ".json"))
            .Where(File.Exists)
            .ToArray();

        await PostProcessMergeAsync(allJsonPaths, progressReporter);

        await progressReporter.ReportAsync(
            "complete",
            $"Finished batch parsing {pdfPaths.Length} issue(s): {successCount} succeeded, {skippedIssues.Count} skipped, {failedIssues.Count} failed. Output written to {outputRoot}",
            current: pdfPaths.Length,
            total: pdfPaths.Length,
            percent: 1);

        if (skippedIssues.Count > 0)
        {
            await Console.Error.WriteLineAsync($"Skipped issues ({skippedIssues.Count}): {string.Join(", ", skippedIssues)}");
        }

        if (failedIssues.Count > 0)
        {
            await Console.Error.WriteLineAsync($"Failed issues ({failedIssues.Count}): {string.Join(", ", failedIssues)}");
        }

        return failedIssues.Count == 0 ? 0 : 1;
    }

    private static async Task PrintUsageAsync()
    {
        await Console.Error.WriteLineAsync("Usage: magparse <path-to-magazine.pdf|path-to-directory> [--output <directory>] [--model-root <directory>] [--progress] [--force] [--max-pages <number>] [--summary-only] [--profile <faster|balanced|quality>] [--ollama-endpoint <uri>] [--ollama-model <model>] [--ollama-page-analysis-model <model>] [--ollama-metadata-model <model>] [--ollama-grouping-model <model>] [--ollama-article-analysis-model <model>] [--render-scale <number>] [--ollama-timeout <seconds>] [--tessdata-path <path>] [--tesseract-language <id>] [--ocr-ml-type-confidence <0-1>] [--ocr-ml-length-confidence <0-1>] [--ocr-ml-max-page-delta <n>] [--ocr-ml-type-override-confidence <0-1>]");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Options:");
        await Console.Error.WriteLineAsync("  <input-path>               Either a single PDF file or a directory containing issue PDFs.");
        await Console.Error.WriteLineAsync("                              Directory mode treats all PDFs as issues of one title and updates shared ML continuity models.");
        await Console.Error.WriteLineAsync("  -o, --output <directory>   Output directory root for all artefacts (JSON, OCR text, page images).");
        await Console.Error.WriteLineAsync("                              Single PDF default: ./<input-file-name>/ in the current directory.");
        await Console.Error.WriteLineAsync("                              Directory default: ./<input-directory>/<issue-name>/ in the current directory.");
        await Console.Error.WriteLineAsync("  --model-root <directory>   Root directory for ML model artifacts (default ./.magparse-models).\n                              Use this to point parser and trainer at the same model store.");
        await Console.Error.WriteLineAsync("  --force                    Ignore existing output, clear the output directory, and reprocess from scratch.");
        await Console.Error.WriteLineAsync("  --progress                 Emit machine-readable JSON progress lines to stdout.");
        await Console.Error.WriteLineAsync("  --max-pages <number>       Limit processing to the first N pages.");
        await Console.Error.WriteLineAsync("  --summary-only             Skip article grouping and article analysis.");
        await Console.Error.WriteLineAsync("  --profile <name>           Set all model options to a preset profile: faster, balanced, or quality.");
        await Console.Error.WriteLineAsync("                              Individual --ollama-*-model flags override the profile when both are supplied.");
        await Console.Error.WriteLineAsync("  --ollama-endpoint <uri>    Override Ollama endpoint (e.g., http://127.0.0.1:11434).");
        await Console.Error.WriteLineAsync("  --ollama-model <model>     Set the default Ollama model for all inference stages.");
        await Console.Error.WriteLineAsync("  --ollama-page-analysis-model <model>");
        await Console.Error.WriteLineAsync("                              Override the vision-capable model used for page analysis.");
        await Console.Error.WriteLineAsync("  --ollama-metadata-model <model>");
        await Console.Error.WriteLineAsync("                              Override the model used for magazine metadata inference.");
        await Console.Error.WriteLineAsync("  --ollama-grouping-model <model>");
        await Console.Error.WriteLineAsync("                              Override the model used for article boundary grouping.");
        await Console.Error.WriteLineAsync("  --ollama-article-analysis-model <model>");
        await Console.Error.WriteLineAsync("                              Override the model used for article analysis.");
        await Console.Error.WriteLineAsync("  --render-scale <number>    Override PDF render scale (e.g., 2.5 for ~180 DPI).");
        await Console.Error.WriteLineAsync("  --ollama-timeout <seconds> Override HTTP timeout for Ollama requests (default 900).");
        await Console.Error.WriteLineAsync("  --tessdata-path <path>     Path to tessdata directory containing language files (default ./tessdata).");
        await Console.Error.WriteLineAsync("  --tesseract-language <id>  Tesseract language code (default eng).");
        await Console.Error.WriteLineAsync("  --ocr-ml-type-confidence <0-1>");
        await Console.Error.WriteLineAsync("                              Minimum type confidence for ML-only bypass (default 0.88).");
        await Console.Error.WriteLineAsync("  --ocr-ml-length-confidence <0-1>");
        await Console.Error.WriteLineAsync("                              Minimum length confidence for ML-only bypass (default 0.75).");
        await Console.Error.WriteLineAsync("  --ocr-ml-max-page-delta <n>");
        await Console.Error.WriteLineAsync("                              Maximum allowed |predictedPages-actualPages| for ML-only bypass (default 1).");
        await Console.Error.WriteLineAsync("  --ocr-ml-type-override-confidence <0-1>");
        await Console.Error.WriteLineAsync("                              Minimum confidence to override draft type with ML type (default 0.92).");
        await Console.Error.WriteLineAsync("  -h, --help                 Show this help text.");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Optional environment variables (overridden by CLI options):");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_ENDPOINT   Defaults to http://127.0.0.1:11434");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_MODEL      Defaults to gemma3:12b");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_PAGE_ANALYSIS_MODEL");
        await Console.Error.WriteLineAsync(DefaultsToModelEnvironmentVariableText);
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_METADATA_MODEL");
        await Console.Error.WriteLineAsync(DefaultsToModelEnvironmentVariableText);
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_GROUPING_MODEL");
        await Console.Error.WriteLineAsync(DefaultsToModelEnvironmentVariableText);
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_ARTICLE_ANALYSIS_MODEL");
        await Console.Error.WriteLineAsync(DefaultsToModelEnvironmentVariableText);
        await Console.Error.WriteLineAsync("  MAGPARSE_RENDER_SCALE      Defaults to 2.5 (roughly 180 DPI)");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_TIMEOUT    Defaults to 900 (seconds, ~15 minutes)");
        await Console.Error.WriteLineAsync("  MAGPARSE_TESSDATA_PATH     Defaults to ./tessdata");
        await Console.Error.WriteLineAsync("  MAGPARSE_TESSERACT_LANGUAGE Defaults to eng");
        await Console.Error.WriteLineAsync("  MAGPARSE_OCR_ML_TYPE_CONFIDENCE Defaults to 0.88");
        await Console.Error.WriteLineAsync("  MAGPARSE_OCR_ML_LENGTH_CONFIDENCE Defaults to 0.75");
        await Console.Error.WriteLineAsync("  MAGPARSE_OCR_ML_MAX_PAGE_DELTA Defaults to 1");
        await Console.Error.WriteLineAsync("  MAGPARSE_OCR_ML_TYPE_OVERRIDE_CONFIDENCE Defaults to 0.92");
    }

    private static string DescribeContinuityModelSelection(string modelDirectory)
    {
        var modelPath = Path.Combine(modelDirectory, "continuity-model.zip");
        var state = File.Exists(modelPath)
            ? "loaded existing continuity model"
            : "no existing continuity model found; a new model will be trained as pages are processed";

        return $"Continuity model directory: {modelDirectory} ({state})";
    }

    private static string DescribeOcrModelSelection(string modelRootDirectory, string resolvedModelDirectory)
    {
        var typeModelPath = Path.Combine(resolvedModelDirectory, "article-type-model.zip");
        var lengthModelPath = Path.Combine(resolvedModelDirectory, "article-length-model.zip");

        var typeState = File.Exists(typeModelPath) ? "present" : "missing";
        var lengthState = File.Exists(lengthModelPath) ? "present" : "missing";

        var trainerDirectory = Path.Combine(modelRootDirectory, "ocr-trainer");
        var source = IsSamePath(resolvedModelDirectory, trainerDirectory)
            ? "ocr-trainer fallback"
            : IsSamePath(resolvedModelDirectory, modelRootDirectory)
                ? "model root fallback"
                : "title-specific directory";

        return $"OCR article model directory: {resolvedModelDirectory} ({source}; article-type-model.zip={typeState}; article-length-model.zip={lengthState})";
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task PostProcessMergeAsync(IEnumerable<string> jsonPaths, IProgressReporter progressReporter)
    {
        var totalMerges = 0;

        foreach (var jsonPath in jsonPaths)
        {
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            MagazineDocument? magazine;
            try
            {
                var json = await File.ReadAllTextAsync(jsonPath);
                magazine = JsonSerializer.Deserialize<MagazineDocument>(json, OutputJsonOptions);
            }
            catch
            {
                continue;
            }

            if (magazine is null || magazine.Articles.Count < 2)
            {
                continue;
            }

            var mergedArticles = MergeAdjacentOutputArticles(magazine.Articles, out var mergeCount);
            if (mergeCount == 0)
            {
                continue;
            }

            totalMerges += mergeCount;

            var updated = new MagazineDocument
            {
                SourcePdfPath = magazine.SourcePdfPath,
                Name = magazine.Name,
                ReleaseDate = magazine.ReleaseDate,
                Publisher = magazine.Publisher,
                Type = magazine.Type,
                CountryOfOrigin = magazine.CountryOfOrigin,
                OriginalPrice = magazine.OriginalPrice,
                CoverImagePath = magazine.CoverImagePath,
                LogoImagePath = magazine.LogoImagePath,
                PageCount = magazine.PageCount,
                Articles = mergedArticles
            };

            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(updated, OutputJsonOptions));

            await progressReporter.ReportAsync(
                "post-process-merge",
                $"Post-process: merged {mergeCount} article pair(s) in {Path.GetFileName(jsonPath)} ({mergedArticles.Count} article(s) remaining)");
        }

        if (totalMerges > 0)
        {
            await progressReporter.ReportAsync(
                "post-process-complete",
                $"Post-process merge pass: {totalMerges} total merge(s) applied.");
        }
    }

    private static IReadOnlyList<ArticleDocument> MergeAdjacentOutputArticles(
        IReadOnlyList<ArticleDocument> articles,
        out int mergeCount)
    {
        mergeCount = 0;
        var result = new List<ArticleDocument>(articles);
        var changed = true;

        while (changed)
        {
            changed = false;
            for (var i = 0; i < result.Count - 1; i++)
            {
                if (!ShouldMergeOutputArticles(result[i], result[i + 1]))
                {
                    continue;
                }

                result[i] = CombineOutputArticles(result[i], result[i + 1]);
                result.RemoveAt(i + 1);
                mergeCount++;
                changed = true;
                break;
            }
        }

        return result;
    }

    private static bool ShouldMergeOutputArticles(ArticleDocument a, ArticleDocument b)
    {
        if (b.StartPage != a.EndPage + 1)
        {
            return false;
        }

        var overlap = ArticleTitleTokenOverlap(a.Title, b.Title);

        // Same title at ≥80% overlap: merge regardless of type (strong signal that the LLM
        // misclassified the type of a continuation page, as with review→news splits).
        if (overlap >= 0.8)
        {
            return true;
        }

        // Moderate overlap: merge only when types are compatible.
        var typeMismatch = a.Type != ArticleType.Unknown
            && b.Type != ArticleType.Unknown
            && a.Type != b.Type;

        return overlap >= 0.35 && !typeMismatch;
    }

    private static ArticleDocument CombineOutputArticles(ArticleDocument a, ArticleDocument b)
    {
        // Prefer the more specific type (review > news > unknown).
        var mergedType = a.Type != ArticleType.Unknown && a.Type != ArticleType.News
            ? a.Type
            : b.Type;

        var mergedImages = a.Images
            .Concat(b.Images)
            .GroupBy(img => img.PageNumber)
            .Select(g => g.First())
            .OrderBy(img => img.PageNumber)
            .ToArray();

        var mergedTags = a.Tags
            .Concat(b.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var mergedSummary = a.Summary.Length >= b.Summary.Length ? a.Summary : b.Summary;

        var totalLen = (double)(a.Text.Length + b.Text.Length);
        var mergedSentimentScore = totalLen > 0
            ? Math.Round(
                (decimal)(((double?)a.SentimentScore ?? 0) * a.Text.Length / totalLen
                    + ((double?)b.SentimentScore ?? 0) * b.Text.Length / totalLen),
                2)
            : a.SentimentScore;
        var mergedSentiment = a.Text.Length >= b.Text.Length ? a.Sentiment : b.Sentiment;

        return new ArticleDocument
        {
            Title = string.IsNullOrWhiteSpace(a.Title) ? b.Title : a.Title,
            Byline = a.Byline ?? b.Byline,
            Subtitle = a.Subtitle ?? b.Subtitle,
            Type = mergedType,
            StartPage = a.StartPage,
            EndPage = b.EndPage,
            Summary = mergedSummary,
            Tags = mergedTags,
            Sentiment = mergedSentiment,
            SentimentScore = mergedSentimentScore,
            Ratings = [.. a.Ratings, .. b.Ratings],
            ReviewedItemReleaseDate = a.ReviewedItemReleaseDate ?? b.ReviewedItemReleaseDate,
            ReviewedItemPrice = a.ReviewedItemPrice ?? b.ReviewedItemPrice,
            Images = mergedImages,
            Text = string.Join(" ", a.Text.TrimEnd(), b.Text.TrimStart()),
            RawText = string.Join("\n", a.RawText.TrimEnd(), b.RawText.TrimStart())
        };
    }

    private static double ArticleTitleTokenOverlap(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }

        var tokensA = TokenizeTitle(a);
        var tokensB = TokenizeTitle(b);

        if (tokensA.Count == 0 || tokensB.Count == 0)
        {
            return 0;
        }

        var intersection = tokensA.Intersect(tokensB, StringComparer.OrdinalIgnoreCase).Count();
        var union = tokensA.Union(tokensB, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> TokenizeTitle(string title)
    {
        return title
            .Split([' ', '-', '_', '/', '.', ',', ':', ';', '!', '?', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();
    }

    private static bool IsIssueAlreadyProcessed(string outputDirectory, string pdfPath)
    {
        return File.Exists(GetIssueJsonPath(outputDirectory, pdfPath));
    }

    private static string GetIssueJsonPath(string outputDirectory, string pdfPath)
    {
        return Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(pdfPath) + ".json");
    }

    private static void ClearOutputDirectory(string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static string GetInProgressDirectory(string finalOutputDirectory)
    {
        var parentDirectory = Path.GetDirectoryName(finalOutputDirectory);
        var leafDirectory = Path.GetFileName(finalOutputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(leafDirectory))
        {
            throw new InvalidOperationException($"Could not resolve inprogress directory for output path {finalOutputDirectory}.");
        }

        return Path.Combine(parentDirectory, "inprogress", leafDirectory);
    }

    private static void PurgeInProgressRoot(string finalOutputDirectory)
    {
        var parentDirectory = Path.GetDirectoryName(finalOutputDirectory);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return;
        }

        ClearOutputDirectory(Path.Combine(parentDirectory, "inprogress"));
    }

    private static void PromoteCompletedIssueOutput(string inProgressDirectory, string finalOutputDirectory)
    {
        if (!Directory.Exists(inProgressDirectory))
        {
            throw new InvalidOperationException($"In-progress output directory does not exist: {inProgressDirectory}");
        }

        if (Directory.Exists(finalOutputDirectory))
        {
            throw new InvalidOperationException($"Final output directory already exists: {finalOutputDirectory}");
        }

        var finalParent = Path.GetDirectoryName(finalOutputDirectory);
        if (!string.IsNullOrWhiteSpace(finalParent))
        {
            Directory.CreateDirectory(finalParent);
        }

        Directory.Move(inProgressDirectory, finalOutputDirectory);
    }

    private static void ValidateForceOutputDirectory(string outputDirectory, string? inputDirectory, string mode)
    {
        if (string.IsNullOrWhiteSpace(inputDirectory))
        {
            return;
        }

        if (string.Equals(
                Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(inputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to use --force in {mode} mode because the output directory matches the input directory.");
        }
    }
}
