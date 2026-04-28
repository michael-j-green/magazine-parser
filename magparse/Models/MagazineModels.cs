namespace magparse.Models;

public sealed class MagazineDocument
{
    public required string SourcePdfPath { get; init; }

    public required string Name { get; init; }

    public DateOnly? ReleaseDate { get; init; }

    public string? Publisher { get; init; }

    public MagazineType Type { get; init; }

    public int PageCount { get; init; }

    public required IReadOnlyList<ArticleDocument> Articles { get; init; }
}

public sealed class ArticleDocument
{
    public required string Title { get; init; }

    public string? Byline { get; init; }

    public string? Subtitle { get; init; }

    public ArticleType Type { get; init; }

    public int StartPage { get; init; }

    public int EndPage { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<string> Tags { get; init; }

    public SentimentLabel Sentiment { get; init; }

    public decimal? SentimentScore { get; init; }

    public required IReadOnlyList<ArticleRating> Ratings { get; init; }

    public required string Text { get; init; }
}

public sealed class ArticleRating
{
    public required string Label { get; init; }

    public required decimal Value { get; init; }

    public decimal? Scale { get; init; }

    public string? Evidence { get; init; }
}

public enum MagazineType
{
    Unknown,
    Computing,
    Gaming,
    Technology,
    Music,
    Film,
    Fashion,
    News,
    GeneralInterest,
    Hobby,
    Lifestyle,
    Business,
    Other
}

public enum ArticleType
{
    Unknown,
    Editorial,
    News,
    Feature,
    Interview,
    Review,
    Preview,
    Guide,
    Letters,
    Advertisement,
    Contents,
    Other
}

public enum SentimentLabel
{
    Unknown,
    VeryNegative,
    Negative,
    Mixed,
    Positive,
    VeryPositive,
    Neutral
}