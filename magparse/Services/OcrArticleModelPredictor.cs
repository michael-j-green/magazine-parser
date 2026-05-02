using System.Text.RegularExpressions;
using magparse.Models;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace magparse.Services;

public interface IOcrArticleModelPredictor
{
    OcrArticlePrediction Predict(
        IReadOnlyList<PageInsight> pages,
        IReadOnlyDictionary<int, OcrReadResult> ocrDiagnosticsByPage,
        ArticleType draftTypeHint);
}

public sealed class OcrArticleModelPredictor : IOcrArticleModelPredictor
{
    private static readonly Regex TokenRegex = new("[A-Za-z0-9]+", RegexOptions.Compiled);

    private readonly PredictionEngine<TypeModelInput, TypeModelOutput>? _typeEngine;
    private readonly PredictionEngine<LengthModelInput, LengthModelOutput>? _lengthEngine;
    private readonly MagparseSettings _settings;

    public OcrArticleModelPredictor(string modelDirectory, MagparseSettings settings)
    {
        _settings = settings;
        var typePath = Path.Combine(modelDirectory, "article-type-model.zip");
        var lengthPath = Path.Combine(modelDirectory, "article-length-model.zip");

        if (File.Exists(typePath))
        {
            var context = new MLContext(seed: 71);
            using var stream = File.OpenRead(typePath);
            var model = context.Model.Load(stream, out _);
            _typeEngine = context.Model.CreatePredictionEngine<TypeModelInput, TypeModelOutput>(model);
        }

        if (File.Exists(lengthPath))
        {
            var context = new MLContext(seed: 73);
            using var stream = File.OpenRead(lengthPath);
            var model = context.Model.Load(stream, out _);
            _lengthEngine = context.Model.CreatePredictionEngine<LengthModelInput, LengthModelOutput>(model);
        }
    }

    public OcrArticlePrediction Predict(
        IReadOnlyList<PageInsight> pages,
        IReadOnlyDictionary<int, OcrReadResult> ocrDiagnosticsByPage,
        ArticleType draftTypeHint)
    {
        if (pages.Count == 0)
        {
            return OcrArticlePrediction.None(draftTypeHint, "empty-pages");
        }

        var pageFeatures = pages
            .Select(page => PageFeatures.From(page, ocrDiagnosticsByPage))
            .ToArray();

        var typePrediction = PredictType(pageFeatures, draftTypeHint);
        var lengthPrediction = PredictLength(pageFeatures, typePrediction.Type, pages.Count);

        var isReviewLike = typePrediction.Type is ArticleType.Review or ArticleType.Preview;
        var pageDelta = Math.Abs(lengthPrediction.ExpectedPageCount - pages.Count);

        var canBypassLlm = !isReviewLike
            && typePrediction.Type != ArticleType.Unknown
            && typePrediction.Confidence >= _settings.OcrMlTypeConfidenceThreshold
            && lengthPrediction.Confidence >= _settings.OcrMlLengthConfidenceThreshold
            && pageDelta <= _settings.OcrMlMaxPageDeltaThreshold;

        return new OcrArticlePrediction(
            Available: _typeEngine is not null || _lengthEngine is not null,
            CanBypassLlm: canBypassLlm,
            Type: typePrediction.Type,
            TypeConfidence: typePrediction.Confidence,
            ExpectedPageCount: lengthPrediction.ExpectedPageCount,
            LengthConfidence: lengthPrediction.Confidence,
            Reason: canBypassLlm ? "high-confidence-ml" : "fallback-llm");
    }

    private (ArticleType Type, double Confidence) PredictType(IReadOnlyList<PageFeatures> pages, ArticleType draftTypeHint)
    {
        if (_typeEngine is null)
        {
            return (draftTypeHint, 0);
        }

        var voteWeights = new Dictionary<ArticleType, double>();
        var totalWeight = 0.0;

        foreach (var feature in pages)
        {
            var output = _typeEngine.Predict(new TypeModelInput
            {
                Label = nameof(ArticleType.Unknown),
                WordCount = feature.WordCount,
                AverageWordLength = feature.AverageWordLength,
                UppercaseRatio = feature.UppercaseRatio,
                DigitRatio = feature.DigitRatio,
                PunctuationRatio = feature.PunctuationRatio,
                NewLineCount = feature.NewLineCount,
                OcrConfidence = feature.OcrConfidence,
                OcrScore = feature.OcrScore,
                TipsKeywordHits = feature.TipsKeywordHits,
                ReviewKeywordHits = feature.ReviewKeywordHits
            });

            var predictedType = TryParseArticleType(output.PredictedLabel) ?? draftTypeHint;
            var confidence = EstimateMulticlassConfidence(output.Score);
            var weight = Math.Max(0.25, feature.WordCount / 250.0);
            var weightedVote = confidence * weight;

            voteWeights[predictedType] = voteWeights.GetValueOrDefault(predictedType) + weightedVote;
            totalWeight += weightedVote;
        }

        if (voteWeights.Count == 0 || totalWeight <= 0)
        {
            return (draftTypeHint, 0);
        }

        var winner = voteWeights.OrderByDescending(entry => entry.Value).First();
        var confidenceScore = Math.Clamp(winner.Value / totalWeight, 0, 1);

        return (winner.Key, confidenceScore);
    }

    private (int ExpectedPageCount, double Confidence) PredictLength(
        IReadOnlyList<PageFeatures> pages,
        ArticleType predictedType,
        int actualPageCount)
    {
        if (_lengthEngine is null)
        {
            return (actualPageCount, 0);
        }

        var row = new LengthModelInput
        {
            Type = predictedType.ToString(),
            PageCount = actualPageCount,
            MeanPageWordCount = (float)pages.Average(feature => feature.WordCount),
            MeanPageScore = (float)pages.Average(feature => feature.OcrScore),
            MeanTipsKeywordHits = (float)pages.Average(feature => feature.TipsKeywordHits),
            MeanReviewKeywordHits = (float)pages.Average(feature => feature.ReviewKeywordHits)
        };

        var predictedScore = _lengthEngine.Predict(row).Score;
        if (float.IsNaN(predictedScore) || float.IsInfinity(predictedScore))
        {
            return (actualPageCount, 0);
        }

        var expectedPages = Math.Max(1, (int)Math.Round(predictedScore));
        var delta = Math.Abs(predictedScore - actualPageCount);
        var confidence = 1.0 / (1.0 + delta);

        return (expectedPages, Math.Clamp(confidence, 0, 1));
    }

    private static ArticleType? TryParseArticleType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<ArticleType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static double EstimateMulticlassConfidence(float[]? rawScores)
    {
        if (rawScores is null || rawScores.Length == 0)
        {
            return 0;
        }

        var finite = rawScores.Where(score => !float.IsNaN(score) && !float.IsInfinity(score)).ToArray();
        if (finite.Length == 0)
        {
            return 0;
        }

        // If scores already look probabilistic, use the top value directly.
        var nonNegative = finite.All(score => score >= 0);
        var total = finite.Sum(score => (double)score);
        if (nonNegative && total > 0.999 && total < 1.001)
        {
            return Math.Clamp(finite.Max(), 0, 1);
        }

        // Otherwise, normalize via softmax.
        var max = finite.Max();
        var exps = finite.Select(score => Math.Exp(score - max)).ToArray();
        var expSum = exps.Sum();
        if (expSum <= 0)
        {
            return 0;
        }

        return Math.Clamp(exps.Max() / expSum, 0, 1);
    }

    private sealed class TypeModelInput
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

    private sealed class TypeModelOutput
    {
        [ColumnName("PredictedLabel")]
        public string? PredictedLabel { get; init; }

        public float[]? Score { get; init; }
    }

    private sealed class LengthModelInput
    {
        public string Type { get; init; } = string.Empty;

        public float PageCount { get; init; }

        public float MeanPageWordCount { get; init; }

        public float MeanPageScore { get; init; }

        public float MeanTipsKeywordHits { get; init; }

        public float MeanReviewKeywordHits { get; init; }
    }

    private sealed class LengthModelOutput
    {
        public float Score { get; init; }
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

        public static PageFeatures From(PageInsight page, IReadOnlyDictionary<int, OcrReadResult> ocrDiagnosticsByPage)
        {
            var text = string.IsNullOrWhiteSpace(page.RawText) ? page.CleanText : page.RawText;
            var tokens = TokenRegex
                .Matches(text)
                .Select(match => match.Value)
                .Where(token => token.Any(char.IsLetterOrDigit))
                .ToArray();

            var chars = Math.Max(1, text.Length);
            var uppercase = text.Count(char.IsUpper);
            var digits = text.Count(char.IsDigit);
            var punctuation = text.Count(ch => ".,;:!?()[]{}<>-/%'\"".Contains(ch));
            var newLines = text.Count(ch => ch == '\n');

            var averageWordLength = tokens.Length == 0 ? 0 : (float)tokens.Average(token => token.Length);
            var tipsHits = CountKeywordHits(text, ["tip", "tips", "walkthrough", "cheat", "level", "strategy", "hint"]);
            var reviewHits = CountKeywordHits(text, ["review", "rating", "score", "graphics", "sound", "playability", "value"]);

            var hasDiagnostics = ocrDiagnosticsByPage.TryGetValue(page.PageNumber, out var diagnostics);

            return new PageFeatures
            {
                WordCount = tokens.Length,
                AverageWordLength = averageWordLength,
                UppercaseRatio = (float)uppercase / chars,
                DigitRatio = (float)digits / chars,
                PunctuationRatio = (float)punctuation / chars,
                NewLineCount = newLines,
                OcrConfidence = hasDiagnostics ? (float)diagnostics!.Confidence : 0,
                OcrScore = hasDiagnostics ? (float)diagnostics!.Score : 0,
                TipsKeywordHits = tipsHits,
                ReviewKeywordHits = reviewHits
            };
        }

        private static float CountKeywordHits(string text, IReadOnlyList<string> keywords)
        {
            var lower = text.ToLowerInvariant();
            var count = 0;
            foreach (var keyword in keywords)
            {
                var index = 0;
                while (true)
                {
                    index = lower.IndexOf(keyword, index, StringComparison.Ordinal);
                    if (index < 0)
                    {
                        break;
                    }

                    count++;
                    index += keyword.Length;
                }
            }

            return count;
        }
    }
}

public static class OcrArticleModelPathResolver
{
    public static string ResolveModelDirectory(string modelRootDirectory, string key)
    {
        var keyDirectory = Path.Combine(modelRootDirectory, key);
        if (ContainsAnyModel(keyDirectory))
        {
            return keyDirectory;
        }

        var trainerDirectory = Path.Combine(modelRootDirectory, "ocr-trainer");
        if (ContainsAnyModel(trainerDirectory))
        {
            return trainerDirectory;
        }

        return modelRootDirectory;
    }

    private static bool ContainsAnyModel(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, "article-type-model.zip"))
            || File.Exists(Path.Combine(directory, "article-length-model.zip"));
    }
}

public sealed record OcrArticlePrediction(
    bool Available,
    bool CanBypassLlm,
    ArticleType Type,
    double TypeConfidence,
    int ExpectedPageCount,
    double LengthConfidence,
    string Reason)
{
    public static OcrArticlePrediction None(ArticleType draftTypeHint, string reason)
    {
        return new OcrArticlePrediction(false, false, draftTypeHint, 0, 0, 0, reason);
    }
}
