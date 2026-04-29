namespace magparse.Services;

public sealed class MagparseSettings
{
    private static readonly Uri DefaultOllamaEndpoint = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", 11434).Uri;

    public required Uri OllamaEndpoint { get; init; }

    public required string OllamaModel { get; init; }

    public required string OllamaPageAnalysisModel { get; init; }

    public required string OllamaMetadataModel { get; init; }

    public required string OllamaGroupingModel { get; init; }

    public required string OllamaArticleAnalysisModel { get; init; }

    public required double RenderScale { get; init; }

    public required int OllamaTimeoutSeconds { get; init; }

    public required string TessDataPath { get; init; }

    public required string TesseractLanguage { get; init; }

    internal static MagparseSettings FromEnvironment(CliOptions options)
    {
        var endpoint = options.OllamaEndpoint ?? Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_ENDPOINT");
        var model = options.OllamaModel ?? Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_MODEL");
        var defaultModel = string.IsNullOrWhiteSpace(model) ? "gemma3:12b" : model.Trim();

        // Apply profile model presets, then allow individual flags to override
        var (profilePage, profileMeta, profileGroup, profileArticle) = ResolveProfileDefaults(options.Profile);
        var pageAnalysisModel = options.OllamaPageAnalysisModel ?? Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_PAGE_ANALYSIS_MODEL") ?? profilePage;
        var metadataModel = options.OllamaMetadataModel ?? Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_METADATA_MODEL") ?? profileMeta;
        var groupingModel = options.OllamaGroupingModel ?? Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_GROUPING_MODEL") ?? profileGroup;
        var articleAnalysisModel = options.OllamaArticleAnalysisModel ?? Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_ARTICLE_ANALYSIS_MODEL") ?? profileArticle;
        var renderScale = options.RenderScale.HasValue ? options.RenderScale.Value.ToString() : Environment.GetEnvironmentVariable("MAGPARSE_RENDER_SCALE");
        var timeout = options.OllamaTimeout.HasValue ? options.OllamaTimeout.Value.ToString() : Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_TIMEOUT");
        var tessDataPath = options.TessDataPath ?? Environment.GetEnvironmentVariable("MAGPARSE_TESSDATA_PATH");
        var tesseractLanguage = options.TesseractLanguage ?? Environment.GetEnvironmentVariable("MAGPARSE_TESSERACT_LANGUAGE");

        return new MagparseSettings
        {
            OllamaEndpoint = endpoint is null ? DefaultOllamaEndpoint : new Uri(endpoint, UriKind.Absolute),
            OllamaModel = defaultModel,
            OllamaPageAnalysisModel = string.IsNullOrWhiteSpace(pageAnalysisModel) ? defaultModel : pageAnalysisModel.Trim(),
            OllamaMetadataModel = string.IsNullOrWhiteSpace(metadataModel) ? defaultModel : metadataModel.Trim(),
            OllamaGroupingModel = string.IsNullOrWhiteSpace(groupingModel) ? defaultModel : groupingModel.Trim(),
            OllamaArticleAnalysisModel = string.IsNullOrWhiteSpace(articleAnalysisModel) ? defaultModel : articleAnalysisModel.Trim(),
            RenderScale = double.TryParse(renderScale, out var parsedScale) && parsedScale > 0 ? parsedScale : 2.5,
            OllamaTimeoutSeconds = int.TryParse(timeout, out var parsedTimeout) && parsedTimeout > 0 ? parsedTimeout : 900,
            TessDataPath = string.IsNullOrWhiteSpace(tessDataPath) ? "./tessdata" : tessDataPath.Trim(),
            TesseractLanguage = string.IsNullOrWhiteSpace(tesseractLanguage) ? "eng" : tesseractLanguage.Trim()
        };
    }

    internal static string? NormalizeProfileName(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return null;
        }

        return profile.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty) switch
        {
            "faster" => "faster",
            "balanced" => "balanced",
            "quality" => "quality",
            "highquality" => "quality",
            _ => null
        };
    }

    private static (string Page, string Meta, string Group, string Article) ResolveProfileDefaults(string? profile)
    {
        return NormalizeProfileName(profile) switch
        {
            "faster" => ("llava:7b", "qwen2.5:3b", "qwen2.5:3b", "qwen2.5:7b"),
            "balanced" => ("llava:13b", "qwen2.5:7b", "qwen2.5:7b", "mistral:7b"),
            "quality" => ("llava:34b", "qwen2.5:14b", "qwen2.5:14b", "mistral:instruct"),
            _ => (null!, null!, null!, null!)
        };
    }
}