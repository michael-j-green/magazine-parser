namespace magparse.Models;

public sealed class PageInsight
{
    public int PageNumber { get; init; }

    public PageKind PageKind { get; init; }

    public ArticleType ArticleTypeHint { get; init; }

    public string? PrimaryHeading { get; init; }

    public IReadOnlyList<string> SecondaryHeadings { get; init; } = [];

    public bool StartsNewArticle { get; init; }

    public bool ContinuesPreviousArticle { get; init; }

    public string CleanText { get; init; } = string.Empty;

    public IReadOnlyList<PageImageAnnotation> ImageAnnotations { get; init; } = [];
}

public sealed class PageImageAnnotation
{
    public string? Description { get; init; }

    public string? Caption { get; init; }
}

public sealed class MagazineMetadataCandidate
{
    public string? Name { get; init; }

    public string? ReleaseDate { get; init; }

    public string? Publisher { get; init; }

    public MagazineType Type { get; init; }

    public string? CountryOfOrigin { get; init; }

    public string? OriginalPrice { get; init; }
}

public sealed class ArticleAnalysisCandidate
{
    public string? Title { get; init; }

    public string? Byline { get; init; }

    public string? Subtitle { get; init; }

    public ArticleType Type { get; init; }

    public string? Summary { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public SentimentLabel Sentiment { get; init; }

    public decimal? SentimentScore { get; init; }

    public IReadOnlyList<ArticleRating> Ratings { get; init; } = [];

    public string? ReviewedItemReleaseDate { get; init; }

    public string? ReviewedItemPrice { get; init; }
}

public sealed class ArticleGroupingCandidate
{
    public IReadOnlyList<ArticleGroupingEntry> Articles { get; init; } = [];
}

public sealed class ArticleGroupingEntry
{
    public int StartPage { get; init; }

    public int EndPage { get; init; }

    public string? Title { get; init; }

    public ArticleType Type { get; init; }
}

public sealed class RenderedPage
{
    public required int PageNumber { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required byte[] JpegBytes { get; init; }
}

public enum PageKind
{
    Unknown,
    Cover,
    Contents,
    Masthead,
    Advertisement,
    Letters,
    Index,
    Article,
    Review,
    Other
}