using System.Text;
using System.Text.RegularExpressions;
using magparse.Models;

namespace magparse.Services;

public sealed class MagazineParserPipeline
{
    private const int MetadataPageLimit = 8;
    private const int ArticleCharacterLimit = 18_000;
    private const int PageOcrCharacterLimit = 16_000;
    private const int PageAnalysisProgressInterval = 5;
    private const int MetadataCoverPageLimit = 4;
    private const int GroupingExcerptDefault = 600;
    private const int GroupingExcerptExtended = 1_500;
    private const double LowOcrPageScoreThreshold = 52.0;
    private const double LowOcrIssueRatioThreshold = 0.35;
    private const string QualityPageModel = "llava:34b";
    private const string QualityGroupingModel = "qwen2.5:14b";
    private const string QualityArticleModel = "mistral:instruct";

    private readonly MagparseSettings _settings;
    private readonly PdfPageRenderer _renderer;
    private readonly OllamaInferenceClient _ollama;
    private readonly TesseractOcrClient _ocr;
    private readonly IProgressReporter _progressReporter;
    private readonly IArticleContinuityPredictor? _continuityPredictor;
    private readonly IOcrArticleModelPredictor? _ocrArticlePredictor;

    public MagazineParserPipeline(
        MagparseSettings settings,
        PdfPageRenderer renderer,
        OllamaInferenceClient ollama,
        TesseractOcrClient ocr,
        IProgressReporter progressReporter,
        IArticleContinuityPredictor? continuityPredictor = null,
        IOcrArticleModelPredictor? ocrArticlePredictor = null)
    {
        _settings = settings;
        _renderer = renderer;
        _ollama = ollama;
        _ocr = ocr;
        _progressReporter = progressReporter;
        _continuityPredictor = continuityPredictor;
        _ocrArticlePredictor = ocrArticlePredictor;
    }

    public async Task<MagazineDocument> ParseAsync(ParseRequest request, CancellationToken cancellationToken)
    {
        var renderedPages = await _renderer.RenderAllPagesAsync(request, _progressReporter, cancellationToken);
        var pageImageBytesByNumber = renderedPages.ToDictionary(page => page.PageNumber, page => page.JpegBytes);

        await SavePageImagesAsync(request.OutputDirectory, renderedPages, cancellationToken);

        var pageInsights = new List<PageInsight>(renderedPages.Count);
        var ocrDiagnosticsByPage = new Dictionary<int, OcrReadResult>(renderedPages.Count);

        await _progressReporter.ReportAsync(
            "analyze-pages",
            $"Running OCR and page analysis for {renderedPages.Count} page(s)",
            current: 0,
            total: renderedPages.Count,
            cancellationToken: cancellationToken);

        // Pre-fetch OCR for the first page immediately. Subsequent pages are kicked off right
        // after the previous page's OCR finishes so Tesseract (CPU-bound) runs concurrently
        // with Ollama (GPU-bound) and doesn't add to the wall-clock time.
        var nextOcrTask = renderedPages.Count > 0
            ? RunTesseractOrNullAsync(renderedPages[0], cancellationToken)
            : Task.FromResult<OcrReadResult?>(null);

        for (var i = 0; i < renderedPages.Count; i++)
        {
            var page = renderedPages[i];
            var preOcrResult = await nextOcrTask;
            var previousInsight = pageInsights.Count > 0 ? pageInsights[^1] : null;

            // Start OCR for the next page immediately so it runs while Ollama processes this one.
            nextOcrTask = i + 1 < renderedPages.Count
                ? RunTesseractOrNullAsync(renderedPages[i + 1], cancellationToken)
                : Task.FromResult<OcrReadResult?>(null);

            var insight = await AnalyzePageAsync(page, preOcrResult, previousInsight, cancellationToken);
            if (preOcrResult is not null)
            {
                ocrDiagnosticsByPage[page.PageNumber] = preOcrResult;
            }
            else
            {
                ocrDiagnosticsByPage[page.PageNumber] = new OcrReadResult(
                    insight.RawText,
                    "vision-model-fallback",
                    0,
                    0,
                    insight.RawText
                        .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                        .Count(token => token.Any(char.IsLetterOrDigit)));
            }
            if (pageInsights.Count > 0)
            {
                insight = ReconcileWithPreviousPage(pageInsights[^1], insight);
            }

            pageInsights.Add(insight);

            if (ShouldReportProgress(page.PageNumber, renderedPages.Count, PageAnalysisProgressInterval))
            {
                await _progressReporter.ReportAsync(
                    "analyze-pages",
                    $"Analyzed page {page.PageNumber} of {renderedPages.Count}",
                    current: page.PageNumber,
                    total: renderedPages.Count,
                    cancellationToken: cancellationToken);
            }
        }

        await _progressReporter.ReportAsync(
            "infer-metadata",
            "Inferring magazine metadata",
            cancellationToken: cancellationToken);

        var metadata = await InferMagazineMetadataAsync(request.PdfPath, pageInsights, cancellationToken);
        var resolvedMagazineName = ResolveMagazineName(metadata.Name, request.PdfPath, pageInsights);

        await WriteOcrOutputAsync(request.OutputDirectory, pageInsights, ocrDiagnosticsByPage);

        var coverImagePath = ResolvePageImagePath(pageInsights, PageKind.Cover);
        var logoImagePath = ResolvePageImagePath(pageInsights, PageKind.Masthead);

        if (request.SummaryOnly)
        {
            await _progressReporter.ReportAsync(
                "summary-only",
                "Skipping article grouping and article analysis because --summary-only was supplied.",
                cancellationToken: cancellationToken);

            return new MagazineDocument
            {
                SourcePdfPath = request.PdfPath,
                Name = resolvedMagazineName,
                ReleaseDate = metadata.ReleaseDate,
                Publisher = metadata.Publisher,
                Type = metadata.Type,
                CountryOfOrigin = metadata.CountryOfOrigin,
                OriginalPrice = metadata.OriginalPrice,
                CoverImagePath = coverImagePath,
                LogoImagePath = logoImagePath,
                PageCount = renderedPages.Count,
                Articles = []
            };
        }

        var heuristicDrafts = BuildArticleDrafts(pageInsights);

        await _progressReporter.ReportAsync(
            "group-articles",
            $"Refining article boundaries across {heuristicDrafts.Count} heuristic draft(s)",
            cancellationToken: cancellationToken);

        var articleDrafts = await RefineArticleDraftsAsync(pageInsights, heuristicDrafts, ocrDiagnosticsByPage, cancellationToken);
        RecordContinuityTrainingExamples(pageInsights, articleDrafts);
        _continuityPredictor?.TrainAndSaveIfNeeded();
        var analyzedArticles = new List<ArticleDocument>(articleDrafts.Count);

        await _progressReporter.ReportAsync(
            "analyze-articles",
            $"Analyzing {articleDrafts.Count} article(s)",
            current: 0,
            total: articleDrafts.Count,
            cancellationToken: cancellationToken);

        for (var articleIndex = 0; articleIndex < articleDrafts.Count; articleIndex++)
        {
            var draft = articleDrafts[articleIndex];
            analyzedArticles.Add(await AnalyzeArticleAsync(draft, pageImageBytesByNumber, ocrDiagnosticsByPage, cancellationToken));
            var resolvedTitle = ResolveDraftTitle(draft);

            await _progressReporter.ReportAsync(
                "analyze-articles",
                $"Analyzed article {articleIndex + 1} of {articleDrafts.Count}: {resolvedTitle ?? "Untitled"}",
                current: articleIndex + 1,
                total: articleDrafts.Count,
                cancellationToken: cancellationToken);
        }

        return new MagazineDocument
        {
            SourcePdfPath = request.PdfPath,
            Name = resolvedMagazineName,
            ReleaseDate = metadata.ReleaseDate,
            Publisher = metadata.Publisher,
            Type = metadata.Type,
            CountryOfOrigin = metadata.CountryOfOrigin,
            OriginalPrice = metadata.OriginalPrice,
            CoverImagePath = coverImagePath,
            LogoImagePath = logoImagePath,
            PageCount = renderedPages.Count,
            Articles = analyzedArticles
        };
    }

    private async Task<OcrReadResult?> RunTesseractOrNullAsync(RenderedPage renderedPage, CancellationToken cancellationToken)
    {
        try
        {
            return await _ocr.ReadTextWithDiagnosticsAsync(renderedPage, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<PageInsight> AnalyzePageAsync(
        RenderedPage renderedPage,
        OcrReadResult? precomputedOcr,
        PageInsight? previousPage,
        CancellationToken cancellationToken)
    {
        string ocrText;
        string ocrSourceLabel;
        var pageModel = _settings.OllamaPageAnalysisModel;

        if (precomputedOcr is not null)
        {
            ocrText = precomputedOcr.Text;
            ocrSourceLabel = "Tesseract OCR";
            if (precomputedOcr.Score < LowOcrPageScoreThreshold)
            {
                pageModel = ResolvePageAnalysisModel(useQualityModel: true);
            }
        }
        else
        {
            // Tesseract was unavailable; fall back to vision model for text extraction.
            ocrText = await _ollama.GenerateResponseAsync(
                "You are an OCR assistant. Extract all visible text from the provided image.",
                "Extract all visible text from this magazine page. Return only the extracted text, nothing else.",
                [renderedPage.JpegBytes],
                _settings.OllamaPageAnalysisModel,
                cancellationToken);
            ocrSourceLabel = "Vision Model OCR Fallback";
            pageModel = ResolvePageAnalysisModel(useQualityModel: true);
        }

        var previousContext = previousPage is null
            ? "No previous page context is available (this is the first page)."
            : $"""
Previous page context:
- pageNumber: {previousPage.PageNumber}
- pageKind: {previousPage.PageKind}
- articleTypeHint: {previousPage.ArticleTypeHint}
- startsNewArticle: {previousPage.StartsNewArticle}
- continuesPreviousArticle: {previousPage.ContinuesPreviousArticle}
- primaryHeading: {previousPage.PrimaryHeading ?? "<none>"}
""";

        var systemPrompt = $"""
You are extracting structured data from a scanned magazine page.
You are given {ocrSourceLabel} and the page image.
Use the OCR text as the primary text source, and use the page image to verify headings, classify the page, and correct only obvious OCR mistakes.
Return valid JSON only.
Be conservative: if the page is mostly an advertisement, contents page, cover, masthead, index, or letters page, classify it that way.
Transcribe visible text as cleanly as possible, correcting obvious OCR noise but not inventing content.
""";

        var prompt = $@"
Analyze page {renderedPage.PageNumber} of a scanned magazine.

Return JSON matching this shape:
{{
    ""pageNumber"": {renderedPage.PageNumber},
    ""pageKind"": ""<one of: unknown|cover|contents|masthead|advertisement|letters|index|article|review|other>"",
    ""articleTypeHint"": ""<one of: unknown|cover|editorial|news|feature|interview|review|preview|tips|guide|letters|advertisement|contents|other>"",
    ""primaryHeading"": ""<the main headline visible on this page, or null if none>"",
    ""secondaryHeadings"": [""<subheading>""],
    ""startsNewArticle"": <true|false>,
    ""continuesPreviousArticle"": <true|false>,
    ""cleanText"": ""<copy ALL body text from the OCR transcription below, correcting obvious noise, preserving full sentences and paragraphs>"",
    ""imageAnnotations"": [
        {{
            ""description"": ""<brief description of the photo, illustration, screenshot or diagram>"",
            ""caption"": ""<caption text clearly associated with this image, or null>""
        }}
    ]
}}

Rules:
- If this page begins an article, set startsNewArticle to true.
- If this page is a continuation of an existing article, set continuesPreviousArticle to true.
- Infer startsNewArticle and continuesPreviousArticle from this page and previous-page context; do not default either field.
- cleanText MUST contain the full body text copied from the OCR transcription. Do NOT summarize it. Do NOT write a placeholder. Copy all readable text.
- primaryHeading MUST be the actual heading text from the page. Do NOT write ""string or null"" literally; use null if there is no heading.
- imageAnnotations should list only actual photographs, illustrations, screenshots or diagrams. Leave the array empty if there are none — do not include decorative borders, text blocks, or page furniture.

{previousContext}

Tesseract OCR transcription follows between the markers below:
<<<OCR>>>
{TrimForModel(ocrText, PageOcrCharacterLimit)}
<<<END_OCR>>>
";

        PageInsight insight;
        try
        {
            insight = await _ollama.GenerateJsonAsync<PageInsight>(
                systemPrompt,
                prompt,
                [renderedPage.JpegBytes],
                pageModel,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _progressReporter.ReportAsync(
                "analyze-pages-warning",
                $"Page {renderedPage.PageNumber}: model JSON parse failed, using OCR-only fallback. {exception.Message}",
                cancellationToken: cancellationToken);

            return BuildFallbackPageInsight(renderedPage.PageNumber, ocrText);
        }

        var primaryHeading = insight.PrimaryHeading;
        if (ShouldTryVisionHeadingFallback(insight))
        {
            var recoveredHeading = await TryRecoverHeadingFromImageAsync(renderedPage, cancellationToken);
            if (!string.IsNullOrWhiteSpace(recoveredHeading))
            {
                primaryHeading = recoveredHeading;
            }
        }

        return new PageInsight
        {
            PageNumber = renderedPage.PageNumber,
            PageKind = insight.PageKind,
            ArticleTypeHint = insight.ArticleTypeHint,
            PrimaryHeading = primaryHeading,
            SecondaryHeadings = insight.SecondaryHeadings,
            StartsNewArticle = insight.StartsNewArticle,
            ContinuesPreviousArticle = insight.ContinuesPreviousArticle,
            CleanText = insight.CleanText?.Trim() ?? string.Empty,
            RawText = ocrText.Trim(),
            ImageAnnotations = insight.ImageAnnotations
        };
    }

    private static PageInsight BuildFallbackPageInsight(int pageNumber, string ocrText)
    {
        return new PageInsight
        {
            PageNumber = pageNumber,
            PageKind = PageKind.Other,
            ArticleTypeHint = ArticleType.Unknown,
            PrimaryHeading = null,
            SecondaryHeadings = [],
            StartsNewArticle = false,
            ContinuesPreviousArticle = false,
            CleanText = ocrText.Trim(),
            RawText = ocrText.Trim(),
            ImageAnnotations = []
        };
    }

    private async Task<MagazineMetadataCandidate> InferMagazineMetadataAsync(string pdfPath, IReadOnlyList<PageInsight> pageInsights, CancellationToken cancellationToken)
    {
        var pagesForMetadata = pageInsights
            .Take(MetadataPageLimit)
            .Select(page => $"Page {page.PageNumber} [{page.PageKind}] Heading: {page.PrimaryHeading ?? "<none>"}\n{TrimForModel(page.CleanText, 2500)}")
            .ToArray();

        var systemPrompt = """
You are inferring the core metadata for a magazine from OCR text.
Return valid JSON only.
If the release date is not explicit, infer only when the evidence is strong enough.
""";

        var prompt = $@"
Source filename: {Path.GetFileName(pdfPath)}

Infer the magazine metadata from the page excerpts below.
Return JSON with this exact shape:
{{
    ""name"": ""<magazine title, or null>"",
    ""releaseDate"": ""<free-form date as printed, e.g. January 1992, 1992-01, 1992, or null>"",
    ""publisher"": ""<publisher name, or null>"",
    ""type"": ""<one of: unknown|computing|gaming|technology|music|film|fashion|news|generalInterest|hobby|lifestyle|business|other>"",
    ""countryOfOrigin"": ""<country where the magazine was published, e.g. UK, US, or null>"",
    ""originalPrice"": ""<cover price as printed on the magazine, e.g. £1.99 or $3.50, or null>""
}}

Page excerpts:
{string.Join("\n\n---\n\n", pagesForMetadata)}
";

        return await _ollama.GenerateJsonAsync<MagazineMetadataCandidate>(systemPrompt, prompt, null, _settings.OllamaMetadataModel, cancellationToken);
    }

    private async Task<IReadOnlyList<ArticleDraft>> RefineArticleDraftsAsync(
        IReadOnlyList<PageInsight> pageInsights,
        IReadOnlyList<ArticleDraft> heuristicDrafts,
        IReadOnlyDictionary<int, OcrReadResult> ocrDiagnosticsByPage,
        CancellationToken cancellationToken)
    {
        var articlePages = pageInsights.Where(IsArticlePage).ToArray();
        if (articlePages.Length == 0)
        {
            return [];
        }

        var lowOcrPageCount = articlePages
            .Count(page => ocrDiagnosticsByPage.TryGetValue(page.PageNumber, out var ocr) && ocr.Score < LowOcrPageScoreThreshold);
        var lowOcrRatio = articlePages.Length == 0 ? 0 : (double)lowOcrPageCount / articlePages.Length;
        var fragmentedHeuristics = heuristicDrafts.Count > Math.Max(2, articlePages.Length / 2);
        var useExtendedContext = lowOcrRatio >= LowOcrIssueRatioThreshold || fragmentedHeuristics;

        var excerptLength = useExtendedContext ? GroupingExcerptExtended : GroupingExcerptDefault;
        var groupingModel = ResolveGroupingModel(useQualityModel: useExtendedContext);

        var pageDescriptions = articlePages.Select(page =>
            $"Page {page.PageNumber}: kind={page.PageKind}; typeHint={page.ArticleTypeHint}; startsNew={page.StartsNewArticle}; continuesPrevious={page.ContinuesPreviousArticle}; heading={page.PrimaryHeading ?? "<none>"}; excerpt={TrimForModel(page.CleanText, excerptLength)}");

        var heuristicSummary = heuristicDrafts.Select((draft, index) =>
            $"Draft {index + 1}: pages {draft.StartPage}-{draft.EndPage}; title={ResolveDraftTitle(draft) ?? "<unknown>"}; type={draft.TypeHint}");

        var systemPrompt = """
You are grouping magazine pages into article boundaries from OCR text.
Return valid JSON only.
Every article or review page should belong to exactly one article range.
Do not create overlapping ranges.
""";

        var prompt = $@"
Infer the final article boundaries from these page descriptions.

Return JSON with this exact shape:
{{
    ""articles"": [
        {{
            ""startPage"": 1,
            ""endPage"": 3,
            ""title"": ""string or null"",
            ""type"": ""unknown|cover|editorial|news|feature|interview|review|preview|tips|guide|letters|advertisement|contents|other""
        }}
    ]
}}

Rules:
- Only include actual article or review content.
- Merge continuation pages into the same article.
- Cover every article/review page exactly once.
- Prefer stable title choices from headings when available.

Heuristic draft boundaries:
{string.Join("\n", heuristicSummary)}

Page descriptions:
{string.Join("\n\n", pageDescriptions)}
";

        var grouping = await _ollama.GenerateJsonAsync<ArticleGroupingCandidate>(systemPrompt, prompt, null, groupingModel, cancellationToken);
        var refinedDrafts = TryBuildDraftsFromGrouping(grouping, articlePages);

        // One bounded retry on likely bad boundaries.
        if (NeedsGroupingRetry(refinedDrafts, heuristicDrafts, articlePages.Length) && groupingModel != ResolveGroupingModel(useQualityModel: true))
        {
            var retryDescriptions = articlePages.Select(page =>
                $"Page {page.PageNumber}: kind={page.PageKind}; typeHint={page.ArticleTypeHint}; startsNew={page.StartsNewArticle}; continuesPrevious={page.ContinuesPreviousArticle}; heading={page.PrimaryHeading ?? "<none>"}; excerpt={TrimForModel(page.CleanText, GroupingExcerptExtended)}");

            var retryPrompt = $@"
Infer the final article boundaries from these page descriptions.

Return JSON with this exact shape:
{{
    ""articles"": [
        {{
            ""startPage"": 1,
            ""endPage"": 3,
            ""title"": ""string or null"",
            ""type"": ""unknown|cover|editorial|news|feature|interview|review|preview|tips|guide|letters|advertisement|contents|other""
        }}
    ]
}}

Rules:
- Only include actual article or review content.
- Merge continuation pages into the same article.
- Cover every article/review page exactly once.
- Prefer stable title choices from headings when available.
- Avoid over-fragmenting article boundaries.

Heuristic draft boundaries:
{string.Join("\n", heuristicSummary)}

Page descriptions:
{string.Join("\n\n", retryDescriptions)}
";

            var retryGrouping = await _ollama.GenerateJsonAsync<ArticleGroupingCandidate>(
                systemPrompt,
                retryPrompt,
                null,
                ResolveGroupingModel(useQualityModel: true),
                cancellationToken);

            var retryDrafts = TryBuildDraftsFromGrouping(retryGrouping, articlePages);
            if (retryDrafts.Count > 0)
            {
                refinedDrafts = retryDrafts;
            }
        }

        var drafts = refinedDrafts.Count == 0 ? heuristicDrafts : refinedDrafts;

        return MergeAdjacentDrafts(drafts);
    }

    private async Task<ArticleDocument> AnalyzeArticleAsync(
        ArticleDraft draft,
        IReadOnlyDictionary<int, byte[]> pageImageBytesByNumber,
        IReadOnlyDictionary<int, OcrReadResult> ocrDiagnosticsByPage,
        CancellationToken cancellationToken)
    {
        var mlPrediction = _ocrArticlePredictor?.Predict(draft.Pages, ocrDiagnosticsByPage, draft.TypeHint);
        var effectiveTypeHint = ResolveEffectiveTypeHint(draft.TypeHint, mlPrediction);
        var draftTitle = ResolveDraftTitle(draft) ?? "Untitled";

        await ReportArticleRoutingDecisionAsync(draft, draftTitle, effectiveTypeHint, mlPrediction, cancellationToken);

        if (mlPrediction is { CanBypassLlm: true })
        {
            return BuildMlOnlyArticleDocument(draft, effectiveTypeHint);
        }

        var articleText = TrimForModel(BuildArticleText(draft.Pages), ArticleCharacterLimit);
        var resolvedDraftTitle = ResolveDraftTitle(draft);
        var titleHint = string.IsNullOrWhiteSpace(resolvedDraftTitle) ? "Unknown" : resolvedDraftTitle;
        var imagePayload = ShouldUseImageAssistedArticleAnalysis(effectiveTypeHint)
            ? draft.Pages
                .Where(page => pageImageBytesByNumber.ContainsKey(page.PageNumber))
                .Select(page => pageImageBytesByNumber[page.PageNumber])
                .Take(4)
                .ToArray()
            : null;

        var systemPrompt = """
You are analyzing a magazine article.
Return valid JSON only.
Extract ratings only when the article clearly contains them.
Use concise, descriptive tags.
""";

        var prompt = $@"
Article title hint: {titleHint}
Page range: {draft.StartPage}-{draft.EndPage}
Initial type hint: {effectiveTypeHint}

Return JSON with this exact shape:
{{
    ""title"": ""<article title, or null>"",
    ""byline"": ""<author name(s), or null>"",
    ""subtitle"": ""<subtitle or standfirst, or null>"",
    ""type"": ""<one of: unknown|cover|editorial|news|feature|interview|review|preview|tips|guide|letters|advertisement|contents|other>"",
    ""summary"": ""<2-4 sentence summary>"",
    ""tags"": [""<tag>""],
    ""sentiment"": ""<one of: unknown|veryNegative|negative|mixed|positive|veryPositive|neutral>"",
    ""sentimentScore"": 0.0,
    ""ratings"": [
        {{
            ""label"": ""<rating label>"",
            ""value"": 0.0,
            ""scale"": 0.0,
            ""evidence"": ""<quote or null>""
        }}
    ],
    ""reviewedItemReleaseDate"": ""<release date of the reviewed game/product if this is a review, e.g. 1991 or November 1991, or null>"",
    ""reviewedItemPrice"": ""<price of the reviewed game/product as printed, e.g. £9.99 or $29.95, or null>""
}}

Rules:
- reviewedItemReleaseDate and reviewedItemPrice only apply when type is review or preview. Set both to null otherwise.
- Extract ratings only when the article clearly contains a numeric score or rating.
- If page images are provided, use them to validate review metadata (ratings, prices, release dates) and sidebars.

Article text:
{articleText}
";

        var articleModel = ResolveArticleAnalysisModel(useQualityModel: false);
        var analysis = await _ollama.GenerateJsonAsync<ArticleAnalysisCandidate>(
            systemPrompt,
            prompt,
            imagePayload,
            articleModel,
            cancellationToken);

        if (NeedsArticleConsistencyRetry(effectiveTypeHint, analysis))
        {
            var retrySystemPrompt = """
You are validating and correcting a prior article analysis result.
Return valid JSON only.
Resolve any type/metadata inconsistencies using the article text and images.
""";

            var retryPrompt = $@"
Re-analyze this article with extra consistency checks.

Title hint: {titleHint}
Page range: {draft.StartPage}-{draft.EndPage}
Draft type hint: {draft.TypeHint}
Draft type hint: {effectiveTypeHint}

Previous analysis result:
{System.Text.Json.JsonSerializer.Serialize(analysis)}

Return JSON with this exact shape:
{{
    ""title"": ""<article title, or null>"",
    ""byline"": ""<author name(s), or null>"",
    ""subtitle"": ""<subtitle or standfirst, or null>"",
    ""type"": ""<one of: unknown|cover|editorial|news|feature|interview|review|preview|tips|guide|letters|advertisement|contents|other>"",
    ""summary"": ""<2-4 sentence summary>"",
    ""tags"": [""<tag>""],
    ""sentiment"": ""<one of: unknown|veryNegative|negative|mixed|positive|veryPositive|neutral>"",
    ""sentimentScore"": 0.0,
    ""ratings"": [
        {{
            ""label"": ""<rating label>"",
            ""value"": 0.0,
            ""scale"": 0.0,
            ""evidence"": ""<quote or null>""
        }}
    ],
    ""reviewedItemReleaseDate"": ""<release date as printed, or null>"",
    ""reviewedItemPrice"": ""<price as printed, or null>""
}}

Consistency requirements:
- Keep type aligned with strong evidence in text/images.
- If type is review or preview, extract ratings/price/release when present.
- If there is no clear evidence, leave optional fields null.

Article text:
{articleText}
";

            analysis = await _ollama.GenerateJsonAsync<ArticleAnalysisCandidate>(
                retrySystemPrompt,
                retryPrompt,
                imagePayload,
                ResolveArticleAnalysisModel(useQualityModel: true),
                cancellationToken);
        }

        return new ArticleDocument
        {
            Title = string.IsNullOrWhiteSpace(analysis.Title) ? titleHint : analysis.Title,
            Byline = analysis.Byline,
            Subtitle = analysis.Subtitle,
            Type = analysis.Type == ArticleType.Unknown ? effectiveTypeHint : analysis.Type,
            StartPage = draft.StartPage,
            EndPage = draft.EndPage,
            Summary = string.IsNullOrWhiteSpace(analysis.Summary) ? "Summary unavailable." : analysis.Summary,
            Tags = analysis.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Sentiment = analysis.Sentiment,
            SentimentScore = analysis.SentimentScore,
            Ratings = analysis.Ratings,
            ReviewedItemReleaseDate = analysis.ReviewedItemReleaseDate,
            ReviewedItemPrice = analysis.ReviewedItemPrice,
            Images = BuildArticleImages(draft.Pages),
            Text = BuildArticleText(draft.Pages),
            RawText = BuildArticleRawText(draft.Pages)
        };
    }

    private string ResolvePageAnalysisModel(bool useQualityModel)
    {
        if (!useQualityModel)
        {
            return _settings.OllamaPageAnalysisModel;
        }

        return string.Equals(_settings.OllamaPageAnalysisModel, QualityPageModel, StringComparison.OrdinalIgnoreCase)
            ? _settings.OllamaPageAnalysisModel
            : QualityPageModel;
    }

    private string ResolveGroupingModel(bool useQualityModel)
    {
        if (!useQualityModel)
        {
            return _settings.OllamaGroupingModel;
        }

        return string.Equals(_settings.OllamaGroupingModel, QualityGroupingModel, StringComparison.OrdinalIgnoreCase)
            ? _settings.OllamaGroupingModel
            : QualityGroupingModel;
    }

    private string ResolveArticleAnalysisModel(bool useQualityModel)
    {
        if (!useQualityModel)
        {
            return _settings.OllamaArticleAnalysisModel;
        }

        return string.Equals(_settings.OllamaArticleAnalysisModel, QualityArticleModel, StringComparison.OrdinalIgnoreCase)
            ? _settings.OllamaArticleAnalysisModel
            : QualityArticleModel;
    }

    private static bool NeedsGroupingRetry(
        IReadOnlyList<ArticleDraft> refinedDrafts,
        IReadOnlyList<ArticleDraft> heuristicDrafts,
        int articlePageCount)
    {
        if (refinedDrafts.Count == 0)
        {
            return true;
        }

        if (articlePageCount <= 0)
        {
            return false;
        }

        var denseFragmentation = refinedDrafts.Count > Math.Max(2, articlePageCount / 2);
        var overSplitComparedToHeuristic = heuristicDrafts.Count > 0 && refinedDrafts.Count > heuristicDrafts.Count + 2;
        return denseFragmentation || overSplitComparedToHeuristic;
    }

    private static bool ShouldUseImageAssistedArticleAnalysis(ArticleType typeHint)
    {
        return typeHint is ArticleType.Review or ArticleType.Preview;
    }

    private static bool NeedsArticleConsistencyRetry(ArticleType draftTypeHint, ArticleAnalysisCandidate analysis)
    {
        var resolvedType = analysis.Type == ArticleType.Unknown ? draftTypeHint : analysis.Type;
        var typeMismatch = draftTypeHint != ArticleType.Unknown
            && analysis.Type != ArticleType.Unknown
            && draftTypeHint != analysis.Type;

        var hasReviewFields = !string.IsNullOrWhiteSpace(analysis.ReviewedItemPrice)
            || !string.IsNullOrWhiteSpace(analysis.ReviewedItemReleaseDate)
            || analysis.Ratings.Count > 0;

        var reviewLikeTags = analysis.Tags.Any(tag =>
            tag.Contains("review", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("rating", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("score", StringComparison.OrdinalIgnoreCase));

        var reviewNeedsMetadata = resolvedType is ArticleType.Review or ArticleType.Preview;
        var missingExpectedReviewData = reviewNeedsMetadata && !hasReviewFields;
        var nonReviewWithReviewData = !reviewNeedsMetadata && hasReviewFields;

        return typeMismatch || missingExpectedReviewData || (nonReviewWithReviewData && reviewLikeTags);
    }

    private async Task ReportArticleRoutingDecisionAsync(
        ArticleDraft draft,
        string resolvedTitle,
        ArticleType effectiveTypeHint,
        OcrArticlePrediction? mlPrediction,
        CancellationToken cancellationToken)
    {
        if (mlPrediction is null || !mlPrediction.Available)
        {
            await _progressReporter.ReportAsync(
                "article-routing",
                $"Article {draft.StartPage}-{draft.EndPage} '{resolvedTitle}': route=llm; reason=model-unavailable; draftType={draft.TypeHint}; effectiveType={effectiveTypeHint}",
                cancellationToken: cancellationToken);
            return;
        }

        var route = mlPrediction.CanBypassLlm ? "ml-only" : "llm";
        await _progressReporter.ReportAsync(
            "article-routing",
            $"Article {draft.StartPage}-{draft.EndPage} '{resolvedTitle}': route={route}; reason={mlPrediction.Reason}; predictedType={mlPrediction.Type}; effectiveType={effectiveTypeHint}; typeConfidence={mlPrediction.TypeConfidence:F2}; expectedPages={mlPrediction.ExpectedPageCount}; actualPages={draft.Pages.Count}; lengthConfidence={mlPrediction.LengthConfidence:F2}",
            cancellationToken: cancellationToken);
    }

    private ArticleType ResolveEffectiveTypeHint(ArticleType draftTypeHint, OcrArticlePrediction? mlPrediction)
    {
        if (mlPrediction is null || mlPrediction.Type == ArticleType.Unknown)
        {
            return draftTypeHint;
        }

        if (draftTypeHint == ArticleType.Unknown)
        {
            return mlPrediction.Type;
        }

        return mlPrediction.TypeConfidence >= _settings.OcrMlTypeOverrideConfidenceThreshold ? mlPrediction.Type : draftTypeHint;
    }

    private static ArticleDocument BuildMlOnlyArticleDocument(ArticleDraft draft, ArticleType typeHint)
    {
        var resolvedTitle = ResolveDraftTitle(draft) ?? "Untitled";
        var articleText = BuildArticleText(draft.Pages);
        var rawText = BuildArticleRawText(draft.Pages);

        return new ArticleDocument
        {
            Title = resolvedTitle,
            Byline = null,
            Subtitle = null,
            Type = typeHint,
            StartPage = draft.StartPage,
            EndPage = draft.EndPage,
            Summary = BuildMlOnlySummary(articleText),
            Tags = BuildMlOnlyTags(typeHint),
            Sentiment = SentimentLabel.Unknown,
            SentimentScore = null,
            Ratings = [],
            ReviewedItemReleaseDate = null,
            ReviewedItemPrice = null,
            Images = BuildArticleImages(draft.Pages),
            Text = articleText,
            RawText = rawText
        };
    }

    private static string BuildMlOnlySummary(string articleText)
    {
        var cleaned = articleText.Trim();
        if (cleaned.Length == 0)
        {
            return "Summary unavailable.";
        }

        var sentenceSeparators = new[] { ". ", "! ", "? ", "\n" };
        var parts = cleaned
            .Split(sentenceSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3)
            .ToArray();

        if (parts.Length == 0)
        {
            return cleaned.Length > 320 ? cleaned[..320] + "..." : cleaned;
        }

        var summary = string.Join(". ", parts).Trim();
        if (!summary.EndsWith('.') && !summary.EndsWith('!') && !summary.EndsWith('?'))
        {
            summary += ".";
        }

        return summary;
    }

    private static IReadOnlyList<string> BuildMlOnlyTags(ArticleType typeHint)
    {
        if (typeHint == ArticleType.Unknown)
        {
            return [];
        }

        return [typeHint.ToString().ToLowerInvariant()];
    }

    private static IReadOnlyList<ArticleDraft> BuildArticleDrafts(IReadOnlyList<PageInsight> pageInsights)
    {
        var drafts = new List<ArticleDraft>();
        ArticleDraft? current = null;

        foreach (var page in pageInsights)
        {
            if (!IsArticlePage(page))
            {
                current = null;
                continue;
            }

            var pageTitle = page.PrimaryHeading?.Trim();
            var startsNew = StartsNewDraft(current, page, pageTitle);

            if (startsNew)
            {
                current = new ArticleDraft(pageTitle, page.ArticleTypeHint, page.PageNumber);
                drafts.Add(current);
            }

            if (current is null)
            {
                continue;
            }

            UpdateDraftFromPage(current, page, pageTitle);
        }

        return drafts.Where(draft => draft.Pages.Count > 0 && !string.IsNullOrWhiteSpace(BuildArticleText(draft.Pages))).ToArray();
    }

    private static bool StartsNewDraft(ArticleDraft? current, PageInsight page, string? pageTitle)
    {
        return current is null ||
               page.StartsNewArticle ||
               (!page.ContinuesPreviousArticle && HasMeaningfullyDifferentHeading(ResolveDraftTitle(current), pageTitle));
    }

    private static void UpdateDraftFromPage(ArticleDraft draft, PageInsight page, string? pageTitle)
    {
        draft.Pages.Add(page);

        if (string.IsNullOrWhiteSpace(draft.Title) && !string.IsNullOrWhiteSpace(pageTitle))
        {
            draft.Title = pageTitle;
        }

        if (draft.TypeHint == ArticleType.Unknown && page.ArticleTypeHint != ArticleType.Unknown)
        {
            draft.TypeHint = page.ArticleTypeHint;
        }
    }

    private static IReadOnlyList<ArticleDraft> TryBuildDraftsFromGrouping(ArticleGroupingCandidate grouping, IReadOnlyList<PageInsight> articlePages)
    {
        if (grouping.Articles.Count == 0)
        {
            return [];
        }

        var articlePagesByNumber = articlePages.ToDictionary(page => page.PageNumber);
        var claimedPages = new HashSet<int>();
        var refinedDrafts = new List<ArticleDraft>(grouping.Articles.Count);

        foreach (var entry in grouping.Articles.OrderBy(article => article.StartPage))
        {
            if (entry.StartPage <= 0 || entry.EndPage < entry.StartPage)
            {
                return [];
            }

            var pages = articlePages
                .Where(page => page.PageNumber >= entry.StartPage && page.PageNumber <= entry.EndPage)
                .OrderBy(page => page.PageNumber)
                .ToArray();

            if (pages.Length == 0)
            {
                return [];
            }

            if (pages.Any(page => !articlePagesByNumber.ContainsKey(page.PageNumber) || !claimedPages.Add(page.PageNumber)))
            {
                return [];
            }

            var draft = new ArticleDraft(entry.Title, entry.Type, pages[0].PageNumber);
            draft.Pages.AddRange(pages);
            refinedDrafts.Add(draft);
        }

        return claimedPages.Count == articlePages.Count ? refinedDrafts : [];
    }

    private static string? ResolveDraftTitle(ArticleDraft? draft)
    {
        if (draft is null)
        {
            return null;
        }

        var fragments = new List<string>();

        AddHeadingFragment(fragments, draft.Title);

        foreach (var page in draft.Pages.Take(2))
        {
            if (page.PageNumber != draft.StartPage && !page.ContinuesPreviousArticle)
            {
                continue;
            }

            AddHeadingFragment(fragments, page.PrimaryHeading);
        }

        return fragments.Count switch
        {
            0 => null,
            1 => fragments[0],
            _ when ShouldCombineHeadingFragments(fragments[0], fragments[1]) => CombineHeadingFragments(fragments[0], fragments[1]),
            _ => fragments.OrderByDescending(fragment => fragment.Length).First()
        };
    }

    private static void AddHeadingFragment(List<string> fragments, string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return;
        }

        var trimmed = heading.Trim();
        if (fragments.Any(existing => !HasMeaningfullyDifferentHeading(existing, trimmed)))
        {
            return;
        }

        fragments.Add(trimmed);
    }

    private static bool ShouldCombineHeadingFragments(string first, string second)
    {
        if (!HasMeaningfullyDifferentHeading(first, second))
        {
            return false;
        }

        return WordCount(first) <= 8 && WordCount(second) <= 8;
    }

    private static string CombineHeadingFragments(string first, string second)
    {
        return string.Join(" ", new[] { first.TrimEnd('-', ' '), second.TrimStart() })
            .Replace("  ", " ")
            .Trim();
    }

    private static int WordCount(string value)
    {
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private async Task<string?> TryRecoverHeadingFromImageAsync(RenderedPage renderedPage, CancellationToken cancellationToken)
    {
        var response = await _ollama.GenerateResponseAsync(
            "You are extracting a headline from a scanned magazine page image.",
            $"Look only at the page image for page {renderedPage.PageNumber}. Return only the main article headline if one is clearly visible, including vertically printed text. If there is no clear main article headline on the page, return exactly: null",
            [renderedPage.JpegBytes],
            _settings.OllamaPageAnalysisModel,
            cancellationToken);

        var trimmed = response.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool ShouldTryVisionHeadingFallback(PageInsight insight)
    {
        if (!string.IsNullOrWhiteSpace(insight.PrimaryHeading))
        {
            return false;
        }

        if (!insight.StartsNewArticle)
        {
            return false;
        }

        return insight.PageKind is PageKind.Article or PageKind.Review;
    }

    private PageInsight ReconcileWithPreviousPage(PageInsight previousPage, PageInsight currentPage)
    {
        if (!IsArticlePage(previousPage) || !IsArticlePage(currentPage))
        {
            return currentPage;
        }

        if (currentPage.PageNumber != previousPage.PageNumber + 1)
        {
            return currentPage;
        }

        var weakHeading = string.IsNullOrWhiteSpace(currentPage.PrimaryHeading) || LooksPlaceholderValue(currentPage.PrimaryHeading);
        var titleOverlap = HeadingTokenOverlap(previousPage.PrimaryHeading, currentPage.PrimaryHeading) >= 0.35;
        var predictedContinuation = MlNetArticleContinuityPredictor.ShouldTreatAsContinuation(
            _continuityPredictor?.PredictContinuationProbability(previousPage, currentPage));

        if (!weakHeading && !titleOverlap && !predictedContinuation)
        {
            return currentPage;
        }

        return new PageInsight
        {
            PageNumber = currentPage.PageNumber,
            PageKind = currentPage.PageKind,
            ArticleTypeHint = currentPage.ArticleTypeHint == ArticleType.Unknown ? previousPage.ArticleTypeHint : currentPage.ArticleTypeHint,
            PrimaryHeading = currentPage.PrimaryHeading,
            SecondaryHeadings = currentPage.SecondaryHeadings,
            StartsNewArticle = false,
            ContinuesPreviousArticle = true,
            CleanText = currentPage.CleanText,
            RawText = currentPage.RawText,
            ImageAnnotations = currentPage.ImageAnnotations
        };
    }

    private static bool IsArticlePage(PageInsight page)
    {
        return page.PageKind is PageKind.Article or PageKind.Review;
    }

    private IReadOnlyList<ArticleDraft> MergeAdjacentDrafts(IReadOnlyList<ArticleDraft> drafts)
    {
        if (drafts.Count <= 1)
        {
            return drafts;
        }

        var merged = new List<ArticleDraft>();

        foreach (var draft in drafts.OrderBy(d => d.StartPage))
        {
            if (merged.Count == 0)
            {
                merged.Add(draft);
                continue;
            }

            var previous = merged[^1];
            if (!ShouldMergeDrafts(previous, draft))
            {
                merged.Add(draft);
                continue;
            }

            previous.Pages.AddRange(draft.Pages);
            if (string.IsNullOrWhiteSpace(previous.Title) && !string.IsNullOrWhiteSpace(draft.Title))
            {
                previous.Title = draft.Title;
            }

            if (previous.TypeHint == ArticleType.Unknown)
            {
                previous.TypeHint = draft.TypeHint;
            }
        }

        return merged;
    }

    private bool ShouldMergeDrafts(ArticleDraft previous, ArticleDraft current)
    {
        if (current.StartPage != previous.EndPage + 1)
        {
            return false;
        }

        var typeMismatch = previous.TypeHint != ArticleType.Unknown
            && current.TypeHint != ArticleType.Unknown
            && previous.TypeHint != current.TypeHint;

        var currentFirstPage = current.Pages.FirstOrDefault();
        if (currentFirstPage is null)
        {
            return false;
        }

        var previousLastPage = previous.Pages.LastOrDefault();
        var predictedContinuation = previousLastPage is null
            ? false
            : MlNetArticleContinuityPredictor.ShouldTreatAsContinuation(
                _continuityPredictor?.PredictContinuationProbability(previousLastPage, currentFirstPage));

        // ML prediction and explicit LLM continuity signal can override a type mismatch —
        // LLMs frequently misclassify the type of continuation pages (e.g. "news" vs "review").
        if (predictedContinuation)
        {
            return true;
        }

        if (currentFirstPage.ContinuesPreviousArticle)
        {
            return true;
        }

        // From here on a type mismatch blocks the merge.
        if (typeMismatch)
        {
            return false;
        }

        if (LooksPlaceholderValue(current.Title) || LooksPlaceholderValue(currentFirstPage.PrimaryHeading))
        {
            return true;
        }

        return HeadingTokenOverlap(ResolveDraftTitle(previous), ResolveDraftTitle(current)) >= 0.35;
    }

    private void RecordContinuityTrainingExamples(IReadOnlyList<PageInsight> pageInsights, IReadOnlyList<ArticleDraft> drafts)
    {
        if (_continuityPredictor is null || drafts.Count == 0)
        {
            return;
        }

        var draftIdByPage = drafts
            .SelectMany((draft, draftIndex) => draft.Pages.Select(page => new { page.PageNumber, DraftIndex = draftIndex }))
            .GroupBy(entry => entry.PageNumber)
            .ToDictionary(group => group.Key, group => group.First().DraftIndex);

        var articlePages = pageInsights
            .Where(IsArticlePage)
            .OrderBy(page => page.PageNumber)
            .ToArray();

        for (var index = 1; index < articlePages.Length; index++)
        {
            var previous = articlePages[index - 1];
            var current = articlePages[index];
            if (current.PageNumber != previous.PageNumber + 1)
            {
                continue;
            }

            if (!draftIdByPage.TryGetValue(previous.PageNumber, out var previousDraft) ||
                !draftIdByPage.TryGetValue(current.PageNumber, out var currentDraft))
            {
                continue;
            }

            _continuityPredictor.RecordExample(previous, current, previousDraft == currentDraft);
        }
    }

    private static bool LooksPlaceholderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains("<string") || normalized.Contains("string or null") || normalized.Contains("<see text above>");
    }

    private static double HeadingTokenOverlap(string? left, string? right)
    {
        var leftTokens = ExtractHeadingTokens(left);
        var rightTokens = ExtractHeadingTokens(right);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersectionCount = leftTokens.Intersect(rightTokens).Count();
        var unionCount = leftTokens.Union(rightTokens).Count();
        return unionCount == 0 ? 0 : (double)intersectionCount / unionCount;
    }

    private static HashSet<string> ExtractHeadingTokens(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return [];
        }

        return Regex.Matches(heading.ToLowerInvariant(), "[a-z0-9]{4,}")
            .Select(match => match.Value.EndsWith('s') ? match.Value[..^1] : match.Value)
            .ToHashSet(StringComparer.Ordinal);
    }
    private static bool HasMeaningfullyDifferentHeading(string? currentTitle, string? candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentTitle))
        {
            return true;
        }

        return !string.Equals(NormalizeHeading(currentTitle), NormalizeHeading(candidateTitle), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHeading(string value)
    {
        return new string(value
            .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
            .Select(char.ToLowerInvariant)
            .ToArray())
            .Trim();
    }

    private static string BuildArticleText(IReadOnlyList<PageInsight> pages)
    {
        return string.Join(
            "\n\n",
            pages
                .Select(page => page.CleanText?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string BuildArticleRawText(IReadOnlyList<PageInsight> pages)
    {
        return string.Join(
            "\n\n",
            pages
                .Select(page => page.RawText?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string ResolveMagazineName(string? inferredName, string pdfPath, IReadOnlyList<PageInsight> pageInsights)
    {
        if (IsPlausibleMagazineName(inferredName))
        {
            return inferredName!.Trim();
        }

        var coverOrMasthead = pageInsights
            .Where(page => page.PageKind is PageKind.Cover or PageKind.Masthead or PageKind.Contents)
            .Take(MetadataCoverPageLimit)
            .Select(page => page.PrimaryHeading)
            .FirstOrDefault(IsPlausibleMagazineName);

        if (IsPlausibleMagazineName(coverOrMasthead))
        {
            return coverOrMasthead!.Trim();
        }

        var fromFileName = BuildNameFromFileName(Path.GetFileNameWithoutExtension(pdfPath));
        if (IsPlausibleMagazineName(fromFileName))
        {
            return fromFileName!.Trim();
        }

        return Path.GetFileNameWithoutExtension(pdfPath);
    }

    private static bool IsPlausibleMagazineName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Trim().ToLowerInvariant();
        if (normalized.Length < 3 || normalized.Length > 80)
        {
            return false;
        }

        if (LooksPlaceholderValue(normalized))
        {
            return false;
        }

        return !normalized.Contains("tesseract") && !normalized.Contains("ocr") && !normalized.Contains("vision model");
    }

    private static string? BuildNameFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var withoutSuffixes = Regex.Replace(fileName, "(issue|hipdf|pdf|scan|scanned|v\\d+|\\d+)$", string.Empty, RegexOptions.IgnoreCase);
        var splitCase = Regex.Replace(withoutSuffixes, "([a-z])([A-Z])", "$1 $2");
        var cleaned = Regex.Replace(splitCase, "[_\\-]+", " ").Trim();
        var squashed = Regex.Replace(cleaned, "\\s+", " ");

        return squashed.Length == 0 ? null : squashed;
    }

    private static string TrimForModel(string input, int maxCharacters)
    {
        if (input.Length <= maxCharacters)
        {
            return input;
        }

        return input[..maxCharacters] + "\n\n[TRUNCATED]";
    }

    private static bool ShouldReportProgress(int current, int total, int interval)
    {
        return current == 1 || current == total || current % interval == 0;
    }

    private static async Task SavePageImagesAsync(string? outputDirectory, IReadOnlyList<RenderedPage> renderedPages, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || renderedPages.Count == 0)
        {
            return;
        }

        var pagesDir = Path.Combine(outputDirectory, "pages");
        Directory.CreateDirectory(pagesDir);

        foreach (var page in renderedPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imagePath = Path.Combine(pagesDir, $"page-{page.PageNumber:D3}.jpg");
            await File.WriteAllBytesAsync(imagePath, page.JpegBytes, cancellationToken);
        }
    }

    private static async Task WriteOcrOutputAsync(
        string? outputDirectory,
        IReadOnlyList<PageInsight> pageInsights,
        IReadOnlyDictionary<int, OcrReadResult>? ocrDiagnosticsByPage)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || pageInsights.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var ocrPath = Path.Combine(outputDirectory, "ocr.txt");

        await using var writer = new StreamWriter(ocrPath, false, Encoding.UTF8);

        foreach (var page in pageInsights)
        {
            await writer.WriteLineAsync($"=== Page {page.PageNumber} ===");
            await writer.WriteLineAsync($"Kind: {page.PageKind}");
            if (ocrDiagnosticsByPage is not null && ocrDiagnosticsByPage.TryGetValue(page.PageNumber, out var diagnostics))
            {
                await writer.WriteLineAsync($"OCR Strategy: {diagnostics.Strategy}");
                await writer.WriteLineAsync($"OCR Mean Confidence: {diagnostics.Confidence:F1}");
                await writer.WriteLineAsync($"OCR Quality Score: {diagnostics.Score:F1}");
                await writer.WriteLineAsync($"OCR Word Count: {diagnostics.WordCount}");
            }
            if (!string.IsNullOrWhiteSpace(page.PrimaryHeading))
            {
                await writer.WriteLineAsync($"Heading: {page.PrimaryHeading}");
            }
            await writer.WriteLineAsync();
            await writer.WriteLineAsync(page.RawText);
            await writer.WriteLineAsync();
        }
    }

    private static string? ResolvePageImagePath(IReadOnlyList<PageInsight> pageInsights, PageKind kind)
    {
        var page = pageInsights.FirstOrDefault(p => p.PageKind == kind);
        return page is null ? null : $"pages/page-{page.PageNumber:D3}.jpg";
    }

    private static IReadOnlyList<ArticleImage> BuildArticleImages(IReadOnlyList<PageInsight> pages)
    {
        return pages
            .Where(page => page.ImageAnnotations.Count > 0)
            .SelectMany(page => page.ImageAnnotations.Select(annotation => new ArticleImage
            {
                PageNumber = page.PageNumber,
                FilePath = $"pages/page-{page.PageNumber:D3}.jpg",
                Caption = annotation.Caption,
                Description = annotation.Description
            }))
            .ToArray();
    }

    private sealed class ArticleDraft
    {
        public ArticleDraft(string? title, ArticleType typeHint, int startPage)
        {
            Title = title;
            TypeHint = typeHint;
            StartPage = startPage;
        }

        public string? Title { get; set; }

        public ArticleType TypeHint { get; set; }

        public int StartPage { get; }

        public int EndPage => Pages[^1].PageNumber;

        public List<PageInsight> Pages { get; } = [];
    }
}




