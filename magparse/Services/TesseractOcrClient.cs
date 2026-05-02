using magparse.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Pix;

namespace magparse.Services;

public sealed class TesseractOcrClient
{
    private const double ColumnFallbackScoreThreshold = 48.0;
    private const int MinMeaningfulWordCount = 80;

    private readonly MagparseSettings _settings;

    public TesseractOcrClient(MagparseSettings settings)
    {
        _settings = settings;
    }

    public async Task<OcrReadResult> ReadTextWithDiagnosticsAsync(RenderedPage page, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var language = MapLanguageCode(_settings.TesseractLanguage);
                using var engine = new Engine(_settings.TessDataPath, language, EngineMode.Default);

                var candidates = new List<OcrCandidate>(capacity: 3)
                {
                    RecognizeCandidate(engine, page.JpegBytes, PageSegMode.Auto, "auto"),
                    RecognizeCandidate(engine, page.JpegBytes, PageSegMode.SingleColumn, "single-column")
                };

                var best = candidates
                    .OrderByDescending(candidate => candidate.Score)
                    .First();

                if (ShouldTryColumnFallback(best, page.JpegBytes))
                {
                    var columnCandidate = RecognizeTwoColumns(engine, page.JpegBytes);
                    if (columnCandidate is not null)
                    {
                        candidates.Add(columnCandidate);
                        best = candidates
                            .OrderByDescending(candidate => candidate.Score)
                            .First();
                    }
                }

                return new OcrReadResult(
                    best.Text,
                    best.Source,
                    best.Confidence,
                    best.Score,
                    best.WordCount);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Tesseract OCR failed for page {page.PageNumber}. Ensure tessdata path is set correctly via --tessdata-path / MAGPARSE_TESSDATA_PATH.",
                    exception);
            }
        }, cancellationToken);
    }

    public async Task<string> ReadTextAsync(RenderedPage page, CancellationToken cancellationToken)
    {
        var result = await ReadTextWithDiagnosticsAsync(page, cancellationToken);
        return result.Text;
    }

    private static OcrCandidate RecognizeCandidate(Engine engine, byte[] imageBytes, PageSegMode pageSegMode, string source)
    {
        using var img = TesseractOCR.Pix.Image.LoadFromMemory(imageBytes);
        using var page = engine.Process(img, pageSegMode);

        var text = page.Text?.Trim() ?? string.Empty;
        var confidence = page.MeanConfidence;
        var score = ComputeScore(text, confidence);

        return new OcrCandidate(text, confidence, score, CountWords(text), source);
    }

    private static OcrCandidate? RecognizeTwoColumns(Engine engine, byte[] imageBytes)
    {
        using var pageImage = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
        var width = pageImage.Width;
        var height = pageImage.Height;
        if (width < 600 || height < 700)
        {
            return null;
        }

        var gutter = Math.Max(16, (int)(width * 0.03));
        var splitX = width / 2;
        var leftWidth = Math.Max(1, splitX - gutter);
        var rightX = Math.Min(width - 1, splitX + gutter);
        var rightWidth = Math.Max(1, width - rightX);

        if (leftWidth < width * 0.3 || rightWidth < width * 0.3)
        {
            return null;
        }

        var leftBytes = CropToJpeg(pageImage, new Rectangle(0, 0, leftWidth, height));
        var rightBytes = CropToJpeg(pageImage, new Rectangle(rightX, 0, rightWidth, height));

        var left = RecognizeCandidate(engine, leftBytes, PageSegMode.SingleColumn, "column-left");
        var right = RecognizeCandidate(engine, rightBytes, PageSegMode.SingleColumn, "column-right");

        var combinedText = string.Join("\n\n", left.Text, right.Text).Trim();
        var combinedWords = left.WordCount + right.WordCount;
        var combinedConfidence = combinedWords > 0
            ? ((left.Confidence * left.WordCount) + (right.Confidence * right.WordCount)) / combinedWords
            : (left.Confidence + right.Confidence) / 2.0;
        var combinedScore = ComputeScore(combinedText, combinedConfidence);

        return new OcrCandidate(combinedText, combinedConfidence, combinedScore, combinedWords, "split-columns");
    }

    private static bool ShouldTryColumnFallback(OcrCandidate best, byte[] imageBytes)
    {
        using var pageImage = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
        var portraitish = pageImage.Height > pageImage.Width;
        var likelyDensePage = best.WordCount >= MinMeaningfulWordCount;
        var qualityLow = best.Score < ColumnFallbackScoreThreshold;

        return portraitish && (qualityLow || likelyDensePage);
    }

    private static byte[] CropToJpeg(SixLabors.ImageSharp.Image<Rgba32> source, Rectangle rect)
    {
        using var clone = source.Clone(ctx => ctx.Crop(rect));
        using var ms = new MemoryStream();
        clone.Save(ms, new JpegEncoder { Quality = 90 });
        return ms.ToArray();
    }

    private static double ComputeScore(string text, double meanConfidence)
    {
        var confidenceScore = Clamp(meanConfidence, 0, 100);
        var wordCount = CountWords(text);
        var readabilityScore = ComputeReadabilityScore(text, wordCount);

        // Confidence dominates, readability adds resilience when confidence alone is misleading.
        return (confidenceScore * 0.65) + (readabilityScore * 0.35);
    }

    private static double ComputeReadabilityScore(string text, int wordCount)
    {
        if (string.IsNullOrWhiteSpace(text) || wordCount == 0)
        {
            return 0;
        }

        var lettersOrDigits = text.Count(char.IsLetterOrDigit);
        var punctuation = text.Count(ch => ".,;:!?-()'\"".Contains(ch));
        var whitespace = text.Count(char.IsWhiteSpace);
        var total = Math.Max(1, text.Length);
        var oddChars = total - lettersOrDigits - punctuation - whitespace;

        var oddRatio = (double)oddChars / total;
        var usefulDensity = (double)(lettersOrDigits + punctuation + whitespace) / total;
        var wordFactor = Math.Min(1.0, wordCount / 140.0);

        var score = (usefulDensity * 100.0)
            - (oddRatio * 180.0)
            + (wordFactor * 20.0);

        return Clamp(score, 0, 100);
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text
            .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Count(token => token.Any(char.IsLetterOrDigit));
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static Language MapLanguageCode(string languageCode)
    {
        // Map common language codes to Language enum values
        // Default to English if not recognized
        return languageCode.ToLowerInvariant() switch
        {
            "eng" => Language.English,
            "fra" => Language.French,
            "deu" => Language.German,
            "ita" => Language.Italian,
            "por" => Language.Portuguese,
            "rus" => Language.Russian,
            "jpn" => Language.Japanese,
            "kor" => Language.Korean,
            _ => Language.English
        };
    }

    private sealed record OcrCandidate(string Text, double Confidence, double Score, int WordCount, string Source);
}

public sealed record OcrReadResult(string Text, string Strategy, double Confidence, double Score, int WordCount);
