using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using magparse.Models;

namespace magparse.Services;

public interface IArticleContinuityPredictor
{
    /// <summary>
    /// Predicts the probability that the current page continues the previous page's article.
    /// </summary>
    double? PredictContinuationProbability(PageInsight previousPage, PageInsight currentPage);

    /// <summary>
    /// Records one labeled continuation example for later model training.
    /// </summary>
    void RecordExample(PageInsight previousPage, PageInsight currentPage, bool sameArticle);

    /// <summary>
    /// Trains and persists the model when enough new examples are available.
    /// </summary>
    void TrainAndSaveIfNeeded();
}

public sealed class MlNetArticleContinuityPredictor : IArticleContinuityPredictor
{
    private const double MergeThreshold = 0.65;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly MLContext _mlContext = new(seed: 13);
    private readonly string _modelPath;
    private readonly string _datasetPath;
    private readonly List<ContinuityTrainingRow> _newExamples = [];
    private readonly object _sync = new();

    private PredictionEngine<ContinuityFeatureRow, ContinuityPrediction>? _engine;

    /// <summary>
    /// Initializes model and dataset paths, then loads an existing model if present.
    /// </summary>
    public MlNetArticleContinuityPredictor(string modelDirectory)
    {
        _modelPath = Path.Combine(modelDirectory, "continuity-model.zip");
        _datasetPath = Path.Combine(modelDirectory, "continuity-training.jsonl");

        Directory.CreateDirectory(modelDirectory);
        TryLoadModel();
    }

    /// <summary>
    /// Returns model probability for continuation, or null when no model is loaded yet.
    /// </summary>
    public double? PredictContinuationProbability(PageInsight previousPage, PageInsight currentPage)
    {
        lock (_sync)
        {
            if (_engine is null)
            {
                return null;
            }

            var prediction = _engine.Predict(ContinuityFeatureRow.FromPages(previousPage, currentPage));
            return prediction.Probability;
        }
    }

    /// <summary>
    /// Queues a labeled example in memory for the next training pass.
    /// </summary>
    public void RecordExample(PageInsight previousPage, PageInsight currentPage, bool sameArticle)
    {
        lock (_sync)
        {
            _newExamples.Add(ContinuityTrainingRow.FromPages(previousPage, currentPage, sameArticle));
        }
    }

    /// <summary>
    /// Trains and saves a model from historical plus new examples when data is sufficient.
    /// </summary>
    public void TrainAndSaveIfNeeded()
    {
        lock (_sync)
        {
            if (_newExamples.Count == 0)
            {
                return;
            }

            var historical = LoadTrainingRows();
            historical.AddRange(_newExamples);

            if (historical.Count < 25 || historical.All(row => row.Label) || historical.All(row => !row.Label))
            {
                PersistTrainingRows(_newExamples);
                _newExamples.Clear();
                return;
            }

            var trainingData = _mlContext.Data.LoadFromEnumerable(historical);

            var pipeline = _mlContext.Transforms.Concatenate(
                    "Features",
                    nameof(ContinuityFeatureRow.HeadingOverlap),
                    nameof(ContinuityFeatureRow.BoundaryTextOverlap),
                    nameof(ContinuityFeatureRow.CurrentLooksPlaceholder),
                    nameof(ContinuityFeatureRow.PreviousLooksPlaceholder),
                    nameof(ContinuityFeatureRow.CurrentStartsNew),
                    nameof(ContinuityFeatureRow.CurrentContinuesPrevious),
                    nameof(ContinuityFeatureRow.PageKindMatch),
                    nameof(ContinuityFeatureRow.TypeHintMatch),
                    nameof(ContinuityFeatureRow.PageGap),
                    nameof(ContinuityFeatureRow.PreviousHasHeading),
                    nameof(ContinuityFeatureRow.CurrentHasHeading))
                .Append(_mlContext.BinaryClassification.Trainers.SdcaLogisticRegression());

            var model = pipeline.Fit(trainingData);
            using var stream = File.Create(_modelPath);
            _mlContext.Model.Save(model, trainingData.Schema, stream);

            _engine = _mlContext.Model.CreatePredictionEngine<ContinuityFeatureRow, ContinuityPrediction>(model);
            PersistTrainingRows(_newExamples);
            _newExamples.Clear();
        }
    }

    /// <summary>
    /// Applies the fixed decision threshold to a predicted continuation probability.
    /// </summary>
    public static bool ShouldTreatAsContinuation(double? probability)
    {
        return probability.HasValue && probability.Value >= MergeThreshold;
    }

    /// <summary>
    /// Loads a previously saved ML.NET model and prepares the prediction engine.
    /// </summary>
    private void TryLoadModel()
    {
        if (!File.Exists(_modelPath))
        {
            return;
        }

        using var stream = File.OpenRead(_modelPath);
        var model = _mlContext.Model.Load(stream, out _);
        _engine = _mlContext.Model.CreatePredictionEngine<ContinuityFeatureRow, ContinuityPrediction>(model);
    }

    /// <summary>
    /// Reads persisted training rows from JSONL, skipping blank or invalid lines.
    /// </summary>
    private List<ContinuityTrainingRow> LoadTrainingRows()
    {
        var rows = new List<ContinuityTrainingRow>();
        if (!File.Exists(_datasetPath))
        {
            return rows;
        }

        foreach (var line in File.ReadLines(_datasetPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var row = JsonSerializer.Deserialize<ContinuityTrainingRow>(line, JsonOptions);
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// Appends training rows to the JSONL dataset file.
    /// </summary>
    private void PersistTrainingRows(IEnumerable<ContinuityTrainingRow> rows)
    {
        using var stream = new StreamWriter(_datasetPath, append: true);
        foreach (var row in rows)
        {
            stream.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private sealed class ContinuityPrediction
    {
        [ColumnName("Probability")]
        public float Probability { get; set; }
    }

    private class ContinuityFeatureRow
    {
        public float HeadingOverlap { get; set; }

        public float BoundaryTextOverlap { get; set; }

        public float CurrentLooksPlaceholder { get; set; }

        public float PreviousLooksPlaceholder { get; set; }

        public float CurrentStartsNew { get; set; }

        public float CurrentContinuesPrevious { get; set; }

        public float PageKindMatch { get; set; }

        public float TypeHintMatch { get; set; }

        public float PageGap { get; set; }

        public float PreviousHasHeading { get; set; }

        public float CurrentHasHeading { get; set; }

        /// <summary>
        /// Builds a numeric feature vector from two neighboring page insights.
        /// </summary>
        public static ContinuityFeatureRow FromPages(PageInsight previousPage, PageInsight currentPage)
        {
            return new ContinuityFeatureRow
            {
                HeadingOverlap = (float)ComputeTokenOverlap(previousPage.PrimaryHeading, currentPage.PrimaryHeading),
                BoundaryTextOverlap = (float)ComputeBoundaryOverlap(previousPage.CleanText, currentPage.CleanText),
                CurrentLooksPlaceholder = LooksPlaceholderValue(currentPage.PrimaryHeading) ? 1f : 0f,
                PreviousLooksPlaceholder = LooksPlaceholderValue(previousPage.PrimaryHeading) ? 1f : 0f,
                CurrentStartsNew = currentPage.StartsNewArticle ? 1f : 0f,
                CurrentContinuesPrevious = currentPage.ContinuesPreviousArticle ? 1f : 0f,
                PageKindMatch = previousPage.PageKind == currentPage.PageKind ? 1f : 0f,
                TypeHintMatch = previousPage.ArticleTypeHint == currentPage.ArticleTypeHint ? 1f : 0f,
                PageGap = Math.Clamp(currentPage.PageNumber - previousPage.PageNumber, 0, 6),
                PreviousHasHeading = string.IsNullOrWhiteSpace(previousPage.PrimaryHeading) ? 0f : 1f,
                CurrentHasHeading = string.IsNullOrWhiteSpace(currentPage.PrimaryHeading) ? 0f : 1f
            };
        }

        /// <summary>
        /// Flags empty or template-like heading text.
        /// </summary>
        private static bool LooksPlaceholderValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var normalized = value.Trim().ToLowerInvariant();
            return normalized.Contains("<string") || normalized.Contains("string or null") || normalized.Contains("<see text above>");
        }

        /// <summary>
        /// Measures overlap between previous-page tail text and current-page lead text.
        /// </summary>
        private static double ComputeBoundaryOverlap(string? previousText, string? currentText)
        {
            if (string.IsNullOrWhiteSpace(previousText) || string.IsNullOrWhiteSpace(currentText))
            {
                return 0;
            }

            var prevTail = TrimToBoundaryTokens(previousText, tail: true, tokenCount: 40);
            var currentHead = TrimToBoundaryTokens(currentText, tail: false, tokenCount: 40);
            return ComputeTokenOverlap(prevTail, currentHead);
        }

        /// <summary>
        /// Returns a fixed-size token window from either start or end of text.
        /// </summary>
        private static string TrimToBoundaryTokens(string text, bool tail, int tokenCount)
        {
            var tokens = Tokenize(text).ToArray();
            if (tokens.Length == 0)
            {
                return string.Empty;
            }

            return tail
                ? string.Join(' ', tokens.Skip(Math.Max(0, tokens.Length - tokenCount)))
                : string.Join(' ', tokens.Take(tokenCount));
        }

        /// <summary>
        /// Computes token Jaccard similarity for two strings.
        /// </summary>
        private static double ComputeTokenOverlap(string? left, string? right)
        {
            var leftTokens = Tokenize(left).ToHashSet(StringComparer.Ordinal);
            var rightTokens = Tokenize(right).ToHashSet(StringComparer.Ordinal);

            if (leftTokens.Count == 0 || rightTokens.Count == 0)
            {
                return 0;
            }

            var intersection = leftTokens.Intersect(rightTokens).Count();
            var union = leftTokens.Union(rightTokens).Count();
            return union == 0 ? 0 : (double)intersection / union;
        }

        /// <summary>
        /// Tokenizes input text to normalized terms used in similarity features.
        /// </summary>
        private static IEnumerable<string> Tokenize(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                yield break;
            }

            foreach (var token in input
                .ToLowerInvariant()
                .Split([' ', '\t', '\r', '\n', '.', ',', ':', ';', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '-', '_', '/', '\\', '|', '+', '*', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Length >= 4)
                {
                    yield return token;
                }
            }
        }
    }

    private sealed class ContinuityTrainingRow : ContinuityFeatureRow
    {
        public bool Label { get; set; }

        /// <summary>
        /// Converts two pages plus ground-truth label into a training row.
        /// </summary>
        public static ContinuityTrainingRow FromPages(PageInsight previousPage, PageInsight currentPage, bool sameArticle)
        {
            var features = ContinuityFeatureRow.FromPages(previousPage, currentPage);
            return new ContinuityTrainingRow
            {
                Label = sameArticle,
                HeadingOverlap = features.HeadingOverlap,
                BoundaryTextOverlap = features.BoundaryTextOverlap,
                CurrentLooksPlaceholder = features.CurrentLooksPlaceholder,
                PreviousLooksPlaceholder = features.PreviousLooksPlaceholder,
                CurrentStartsNew = features.CurrentStartsNew,
                CurrentContinuesPrevious = features.CurrentContinuesPrevious,
                PageKindMatch = features.PageKindMatch,
                TypeHintMatch = features.TypeHintMatch,
                PageGap = features.PageGap,
                PreviousHasHeading = features.PreviousHasHeading,
                CurrentHasHeading = features.CurrentHasHeading
            };
        }
    }
}

public static class ContinuityModelPathResolver
{
    /// <summary>
    /// Resolves the model directory path for a title or batch key.
    /// </summary>
    public static string ResolveModelDirectory(string rootDirectory, string titleOrKey)
    {
        var sanitized = SanitizeSegment(titleOrKey);
        return Path.Combine(rootDirectory, sanitized);
    }

    /// <summary>
    /// Normalizes a key into a safe single path segment.
    /// </summary>
    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(character => invalidChars.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "default" : cleaned;
    }
}
