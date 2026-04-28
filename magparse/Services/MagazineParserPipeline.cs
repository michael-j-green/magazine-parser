using System.Text;
using magparse.Models;

namespace magparse.Services;

public sealed class MagazineParserPipeline
{
    private const int MetadataPageLimit = 8;
    private const int ArticleCharacterLimit = 18_000;
    private const int PageAnalysisProgressInterval = 5;

    private readonly PdfPageRenderer _renderer;
    private readonly OllamaInferenceClient _ollama;
    private readonly IProgressReporter _progressReporter;

    public MagazineParserPipeline(PdfPageRenderer renderer, OllamaInferenceClient ollama, IProgressReporter progressReporter)
    {
        _renderer = renderer;
        _ollama = ollama;
        _progressReporter = progressReporter;
    }

    public async Task<MagazineDocument> ParseAsync(ParseRequest request, CancellationToken cancellationToken)
    {
        var renderedPages = await _renderer.RenderAllPagesAsync(request, _progressReporter, cancellationToken);
        var pageInsights = new List<PageInsight>(renderedPages.Count);

        await _progressReporter.ReportAsync(
            "analyze-pages",
            $"Running OCR and page analysis for {renderedPages.Count} page(s)",
            current: 0,
            total: renderedPages.Count,
            cancellationToken: cancellationToken);

        foreach (var page in renderedPages)
        {
            pageInsights.Add(await AnalyzePageAsync(page, cancellationToken));

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

        if (request.SummaryOnly)
        {
            await _progressReporter.ReportAsync(
                "summary-only",
                "Skipping article grouping and article analysis because --summary-only was supplied.",
                cancellationToken: cancellationToken);

            var summaryDocument = new MagazineDocument
            {
                SourcePdfPath = request.PdfPath,
                Name = string.IsNullOrWhiteSpace(metadata.Name) ? Path.GetFileNameWithoutExtension(request.PdfPath) : metadata.Name,
                ReleaseDate = metadata.ReleaseDate,
                Publisher = metadata.Publisher,
                Type = metadata.Type,
                PageCount = renderedPages.Count,
                Articles = []
            };

            await WriteRawOutputAsync(request.RawOutputDirectory, request.PdfPath, pageInsights);
            return summaryDocument;
        }

        var heuristicDrafts = BuildArticleDrafts(pageInsights);

        await _progressReporter.ReportAsync(
            "group-articles",
            $"Refining article boundaries across {heuristicDrafts.Count} heuristic draft(s)",
            cancellationToken: cancellationToken);

        var articleDrafts = await RefineArticleDraftsAsync(pageInsights, heuristicDrafts, cancellationToken);
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
            analyzedArticles.Add(await AnalyzeArticleAsync(draft, cancellationToken));

            await _progressReporter.ReportAsync(
                "analyze-articles",
                $"Analyzed article {articleIndex + 1} of {articleDrafts.Count}: {draft.Title ?? "Untitled"}",
                current: articleIndex + 1,
                total: articleDrafts.Count,
                cancellationToken: cancellationToken);
        }

        var finalDocument = new MagazineDocument
        {
            SourcePdfPath = request.PdfPath,
            Name = string.IsNullOrWhiteSpace(metadata.Name) ? Path.GetFileNameWithoutExtension(request.PdfPath) : metadata.Name,
            ReleaseDate = metadata.ReleaseDate,
            Publisher = metadata.Publisher,
            Type = metadata.Type,
            PageCount = renderedPages.Count,
            Articles = analyzedArticles
        };

        await WriteRawOutputAsync(request.RawOutputDirectory, request.PdfPath, pageInsights);
        return finalDocument;
    }

    private async Task<PageInsight> AnalyzePageAsync(RenderedPage renderedPage, CancellationToken cancellationToken)
    {
        var systemPrompt = """
You are extracting structured data from a scanned magazine page.
Return valid JSON only.
Be conservative: if the page is mostly an advertisement, contents page, cover, masthead, index, or letters page, classify it that way.
Transcribe visible text as cleanly as possible, correcting obvious OCR noise but not inventing content.
""";

        var prompt = $@"
Analyze page {renderedPage.PageNumber} of a scanned magazine.

Return JSON matching this shape:
{{
    ""pageNumber"": {renderedPage.PageNumber},
    ""pageKind"": ""unknown|cover|contents|masthead|advertisement|letters|index|article|review|other"",
    ""articleTypeHint"": ""unknown|editorial|news|feature|interview|review|preview|guide|letters|advertisement|contents|other"",
    ""primaryHeading"": ""string or null"",
    ""secondaryHeadings"": [""string""],
    ""startsNewArticle"": true,
    ""continuesPreviousArticle"": false,
    ""cleanText"": ""full cleaned OCR text""
}}

Rules:
- If this page begins an article, set startsNewArticle to true.
- If this page is a continuation of an existing article, set continuesPreviousArticle to true.
- cleanText should preserve the useful reading text and omit obvious decorative scraps when possible.
- Use null when no reliable primary heading exists.
";

        var insight = await _ollama.GenerateJsonAsync<PageInsight>(
            systemPrompt,
            prompt,
            [renderedPage.JpegBytes],
            cancellationToken);

        return new PageInsight
        {
            PageNumber = renderedPage.PageNumber,
            PageKind = insight.PageKind,
            ArticleTypeHint = insight.ArticleTypeHint,
            PrimaryHeading = insight.PrimaryHeading,
            SecondaryHeadings = insight.SecondaryHeadings,
            StartsNewArticle = insight.StartsNewArticle,
            ContinuesPreviousArticle = insight.ContinuesPreviousArticle,
            CleanText = insight.CleanText?.Trim() ?? string.Empty
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
    ""name"": ""string or null"",
    ""releaseDate"": ""YYYY-MM-DD or null"",
    ""publisher"": ""string or null"",
    ""type"": ""unknown|computing|gaming|technology|music|film|fashion|news|generalInterest|hobby|lifestyle|business|other""
}}

Page excerpts:
{string.Join("\n\n---\n\n", pagesForMetadata)}
";

        return await _ollama.GenerateJsonAsync<MagazineMetadataCandidate>(systemPrompt, prompt, null, cancellationToken);
    }

    private async Task<IReadOnlyList<ArticleDraft>> RefineArticleDraftsAsync(
        IReadOnlyList<PageInsight> pageInsights,
        IReadOnlyList<ArticleDraft> heuristicDrafts,
        CancellationToken cancellationToken)
    {
        var articlePages = pageInsights.Where(IsArticlePage).ToArray();
        if (articlePages.Length == 0)
        {
            return [];
        }

        var pageDescriptions = articlePages.Select(page =>
            $"Page {page.PageNumber}: kind={page.PageKind}; typeHint={page.ArticleTypeHint}; startsNew={page.StartsNewArticle}; continuesPrevious={page.ContinuesPreviousArticle}; heading={page.PrimaryHeading ?? "<none>"}; excerpt={TrimForModel(page.CleanText, 600)}");

        var heuristicSummary = heuristicDrafts.Select((draft, index) =>
            $"Draft {index + 1}: pages {draft.StartPage}-{draft.EndPage}; title={draft.Title ?? "<unknown>"}; type={draft.TypeHint}");

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
            ""type"": ""unknown|editorial|news|feature|interview|review|preview|guide|letters|advertisement|contents|other""
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

        var grouping = await _ollama.GenerateJsonAsync<ArticleGroupingCandidate>(systemPrompt, prompt, null, cancellationToken);
        var refinedDrafts = TryBuildDraftsFromGrouping(grouping, articlePages);

        return refinedDrafts.Count == 0 ? heuristicDrafts : refinedDrafts;
    }

    private async Task<ArticleDocument> AnalyzeArticleAsync(ArticleDraft draft, CancellationToken cancellationToken)
    {
        var articleText = TrimForModel(BuildArticleText(draft.Pages), ArticleCharacterLimit);
        var titleHint = string.IsNullOrWhiteSpace(draft.Title) ? "Unknown" : draft.Title;

        var systemPrompt = """
You are analyzing a magazine article.
Return valid JSON only.
Extract ratings only when the article clearly contains them.
Use concise, descriptive tags.
""";

        var prompt = $@"
Article title hint: {titleHint}
Page range: {draft.StartPage}-{draft.EndPage}
Initial type hint: {draft.TypeHint}

Return JSON with this exact shape:
{{
    ""title"": ""string or null"",
    ""byline"": ""string or null"",
    ""subtitle"": ""string or null"",
    ""type"": ""unknown|editorial|news|feature|interview|review|preview|guide|letters|advertisement|contents|other"",
    ""summary"": ""string"",
    ""tags"": [""string""],
    ""sentiment"": ""unknown|veryNegative|negative|mixed|positive|veryPositive|neutral"",
    ""sentimentScore"": 0.0,
    ""ratings"": [
        {{
            ""label"": ""string"",
            ""value"": 0.0,
            ""scale"": 0.0,
            ""evidence"": ""string or null""
        }}
    ]
}}

Article text:
{articleText}
";

        var analysis = await _ollama.GenerateJsonAsync<ArticleAnalysisCandidate>(systemPrompt, prompt, null, cancellationToken);

        return new ArticleDocument
        {
            Title = string.IsNullOrWhiteSpace(analysis.Title) ? titleHint : analysis.Title,
            Byline = analysis.Byline,
            Subtitle = analysis.Subtitle,
            Type = analysis.Type == ArticleType.Unknown ? draft.TypeHint : analysis.Type,
            StartPage = draft.StartPage,
            EndPage = draft.EndPage,
            Summary = string.IsNullOrWhiteSpace(analysis.Summary) ? "Summary unavailable." : analysis.Summary,
            Tags = analysis.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Sentiment = analysis.Sentiment,
            SentimentScore = analysis.SentimentScore,
            Ratings = analysis.Ratings,
            Text = BuildArticleText(draft.Pages)
        };
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
               (!page.ContinuesPreviousArticle && HasMeaningfullyDifferentHeading(current.Title, pageTitle));
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

    private static bool IsArticlePage(PageInsight page)
    {
        return page.PageKind is PageKind.Article or PageKind.Review;
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

    private static async Task WriteRawOutputAsync(string? outputDirectory, string pdfPath, IReadOnlyList<PageInsight> pageInsights)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || pageInsights.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var baseName = Path.GetFileNameWithoutExtension(pdfPath);
        var ocrPath = Path.Combine(outputDirectory, $"{baseName}_raw-ocr.txt");

        await using var writer = new StreamWriter(ocrPath, false, Encoding.UTF8);

        foreach (var page in pageInsights)
        {
            await writer.WriteLineAsync($"=== Page {page.PageNumber} ===");
            await writer.WriteLineAsync($"Kind: {page.PageKind}");
            if (!string.IsNullOrWhiteSpace(page.PrimaryHeading))
            {
                await writer.WriteLineAsync($"Heading: {page.PrimaryHeading}");
            }
            await writer.WriteLineAsync();
            await writer.WriteLineAsync(page.CleanText);
            await writer.WriteLineAsync();
        }
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