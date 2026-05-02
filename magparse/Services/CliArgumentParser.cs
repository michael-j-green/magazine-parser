namespace magparse.Services;

internal static class CliArgumentParser
{
    public static bool TryParse(string[] args, out CliOptions options, out string? errorMessage)
    {
        var state = new CliParseState();
        var index = 0;

        while (index < args.Length)
        {
            var argument = args[index];
            if (TryHandleFlag(argument, state, ref index))
            {
                errorMessage = null;
                continue;
            }

            if (TryRequiresValue(argument))
            {
                if (!TryHandleValueOption(args, argument, state, ref index, out errorMessage))
                {
                    options = CliOptions.Empty;
                    return false;
                }

                continue;
            }

            if (argument.StartsWith('-'))
            {
                options = CliOptions.Empty;
                errorMessage = $"Unknown option: {argument}";
                return false;
            }

            if (state.InputPath is not null)
            {
                options = CliOptions.Empty;
                errorMessage = "Only one input path may be supplied.";
                return false;
            }

            state.InputPath = argument;
            index++;
        }

        if (!state.ShowHelp && string.IsNullOrWhiteSpace(state.InputPath))
        {
            options = CliOptions.Empty;
            errorMessage = "An input path is required.";
            return false;
        }

        options = new CliOptions
        {
            InputPath = state.InputPath ?? string.Empty,
            OutputPath = state.OutputPath,
            ModelRootPath = state.ModelRootPath,
            EmitStructuredProgress = state.EmitStructuredProgress,
            Force = state.Force,
            ShowHelp = state.ShowHelp,
            MaxPages = state.MaxPages,
            SummaryOnly = state.SummaryOnly,
            OllamaEndpoint = state.OllamaEndpoint,
            OllamaModel = state.OllamaModel,
            OllamaPageAnalysisModel = state.OllamaPageAnalysisModel,
            OllamaMetadataModel = state.OllamaMetadataModel,
            OllamaGroupingModel = state.OllamaGroupingModel,
            OllamaArticleAnalysisModel = state.OllamaArticleAnalysisModel,
            RenderScale = state.RenderScale,
            OllamaTimeout = state.OllamaTimeout,
            TessDataPath = state.TessDataPath,
            TesseractLanguage = state.TesseractLanguage,
            Profile = state.Profile,
            OcrMlTypeConfidenceThreshold = state.OcrMlTypeConfidenceThreshold,
            OcrMlLengthConfidenceThreshold = state.OcrMlLengthConfidenceThreshold,
            OcrMlMaxPageDeltaThreshold = state.OcrMlMaxPageDeltaThreshold,
            OcrMlTypeOverrideConfidenceThreshold = state.OcrMlTypeOverrideConfidenceThreshold
        };
        errorMessage = null;
        return true;
    }

    private static bool TryHandleFlag(string argument, CliParseState state, ref int index)
    {
        switch (argument)
        {
            case "-h":
            case "--help":
                state.ShowHelp = true;
                index++;
                return true;
            case "--progress":
                state.EmitStructuredProgress = true;
                index++;
                return true;
            case "--force":
                state.Force = true;
                index++;
                return true;
            case "--summary-only":
                state.SummaryOnly = true;
                index++;
                return true;
            default:
                return false;
        }
    }

    private static bool TryRequiresValue(string argument)
    {
        return argument is "-o" or "--output" or "--model-root" or "--max-pages" or "--ollama-endpoint" or "--ollama-model" or "--ollama-page-analysis-model" or "--ollama-metadata-model" or "--ollama-grouping-model" or "--ollama-article-analysis-model" or "--render-scale" or "--ollama-timeout" or "--tessdata-path" or "--tesseract-language" or "--profile" or "--ocr-ml-type-confidence" or "--ocr-ml-length-confidence" or "--ocr-ml-max-page-delta" or "--ocr-ml-type-override-confidence";
    }

    private static bool TryHandleValueOption(string[] args, string argument, CliParseState state, ref int index, out string? errorMessage)
    {
        if (!TryReadValue(args, index, argument, out var value, out errorMessage))
        {
            return false;
        }

        if (TryHandleStringOption(argument, value, state))
        {
            index += 2;
            errorMessage = null;
            return true;
        }

        if (argument is "--profile")
        {
            return CompleteValidatedOption(TryHandleProfileOption(value, state, out errorMessage), ref index, out errorMessage, errorMessage);
        }

        if (argument is "--ollama-endpoint")
        {
            return CompleteValidatedOption(TryHandleEndpointOption(argument, value, state, out errorMessage), ref index, out errorMessage, errorMessage);
        }

        if (argument is "--render-scale")
        {
            return CompleteValidatedOption(TryHandleRenderScaleOption(argument, value, state, out errorMessage), ref index, out errorMessage, errorMessage);
        }

        if (argument is "--ollama-timeout")
        {
            return CompleteValidatedOption(TryHandleTimeoutOption(argument, value, state, out errorMessage), ref index, out errorMessage, errorMessage);
        }

        if (argument is "--ocr-ml-max-page-delta")
        {
            return CompleteValidatedOption(TryHandleOcrMlMaxPageDeltaOption(argument, value, state, out errorMessage), ref index, out errorMessage, errorMessage);
        }

        if (argument is "--ocr-ml-type-confidence" or "--ocr-ml-length-confidence" or "--ocr-ml-type-override-confidence")
        {
            return CompleteValidatedOption(TryHandleOcrMlConfidenceOption(argument, value, state, out errorMessage), ref index, out errorMessage, errorMessage);
        }

        return CompleteValidatedOption(TryHandleMaxPagesOption(argument, value, state, out errorMessage), ref index, out errorMessage, errorMessage);
    }

    private static bool TryHandleOcrMlConfidenceOption(string argument, string value, CliParseState state, out string? errorMessage)
    {
        if (!double.TryParse(value, out var parsed) || parsed <= 0 || parsed > 1)
        {
            errorMessage = $"The {argument} option requires a decimal value in the range (0, 1].";
            return false;
        }

        switch (argument)
        {
            case "--ocr-ml-type-confidence":
                state.OcrMlTypeConfidenceThreshold = parsed;
                break;
            case "--ocr-ml-length-confidence":
                state.OcrMlLengthConfidenceThreshold = parsed;
                break;
            case "--ocr-ml-type-override-confidence":
                state.OcrMlTypeOverrideConfidenceThreshold = parsed;
                break;
            default:
                errorMessage = $"Unknown option: {argument}";
                return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool TryHandleOcrMlMaxPageDeltaOption(string argument, string value, CliParseState state, out string? errorMessage)
    {
        if (argument is not "--ocr-ml-max-page-delta")
        {
            errorMessage = $"Unknown option: {argument}";
            return false;
        }

        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            errorMessage = "The --ocr-ml-max-page-delta option requires a non-negative integer.";
            return false;
        }

        state.OcrMlMaxPageDeltaThreshold = parsed;
        errorMessage = null;
        return true;
    }

    private static bool CompleteValidatedOption(bool success, ref int index, out string? errorMessage, string? validationError)
    {
        errorMessage = validationError;
        if (!success)
        {
            return false;
        }

        index += 2;
        errorMessage = null;
        return true;
    }

    private static bool TryHandleStringOption(string argument, string value, CliParseState state)
    {
        switch (argument)
        {
            case "-o":
            case "--output":
                state.OutputPath = value;
                return true;
            case "--model-root":
                state.ModelRootPath = value;
                return true;
            case "--ollama-model":
                state.OllamaModel = value;
                return true;
            case "--ollama-page-analysis-model":
                state.OllamaPageAnalysisModel = value;
                return true;
            case "--ollama-metadata-model":
                state.OllamaMetadataModel = value;
                return true;
            case "--ollama-grouping-model":
                state.OllamaGroupingModel = value;
                return true;
            case "--ollama-article-analysis-model":
                state.OllamaArticleAnalysisModel = value;
                return true;
            case "--tessdata-path":
                state.TessDataPath = value;
                return true;
            case "--tesseract-language":
                state.TesseractLanguage = value;
                return true;
            default:
                return false;
        }
    }

    private static bool TryHandleProfileOption(string value, CliParseState state, out string? errorMessage)
    {
        var normalized = MagparseSettings.NormalizeProfileName(value);
        if (normalized is null)
        {
            errorMessage = "The --profile option must be one of: faster, balanced, quality, highquality, or high-quality.";
            return false;
        }

        state.Profile = normalized;
        errorMessage = null;
        return true;
    }

    private static bool TryHandleEndpointOption(string argument, string value, CliParseState state, out string? errorMessage)
    {
        if (argument is not "--ollama-endpoint")
        {
            errorMessage = null;
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            errorMessage = "The --ollama-endpoint option requires a valid URI.";
            return false;
        }

        state.OllamaEndpoint = value;
        errorMessage = null;
        return true;
    }

    private static bool TryHandleRenderScaleOption(string argument, string value, CliParseState state, out string? errorMessage)
    {
        if (argument is not "--render-scale")
        {
            errorMessage = null;
            return false;
        }

        if (!double.TryParse(value, out var parsedScale) || parsedScale <= 0)
        {
            errorMessage = "The --render-scale option requires a positive number.";
            return false;
        }

        state.RenderScale = parsedScale;
        errorMessage = null;
        return true;
    }

    private static bool TryHandleTimeoutOption(string argument, string value, CliParseState state, out string? errorMessage)
    {
        if (argument is not "--ollama-timeout")
        {
            errorMessage = null;
            return false;
        }

        if (!int.TryParse(value, out var parsedTimeout) || parsedTimeout <= 0)
        {
            errorMessage = "The --ollama-timeout option requires a positive integer (seconds).";
            return false;
        }

        state.OllamaTimeout = parsedTimeout;
        errorMessage = null;
        return true;
    }

    private static bool TryHandleMaxPagesOption(string argument, string value, CliParseState state, out string? errorMessage)
    {
        if (argument is not "--max-pages")
        {
            errorMessage = $"Unknown option: {argument}";
            return false;
        }

        if (!int.TryParse(value, out var parsedMaxPages) || parsedMaxPages <= 0)
        {
            errorMessage = "The --max-pages option requires a positive integer.";
            return false;
        }

        state.MaxPages = parsedMaxPages;
        errorMessage = null;
        return true;
    }

    private static bool TryReadValue(string[] args, int index, string optionName, out string value, out string? errorMessage)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            errorMessage = $"The {optionName} option requires a value.";
            return false;
        }

        value = args[index + 1];
        errorMessage = null;
        return true;
    }
}

internal sealed class CliOptions
{
    public static CliOptions Empty => new() { InputPath = string.Empty, ShowHelp = false };

    public required string InputPath { get; init; }

    public string? OutputPath { get; init; }

    public string? ModelRootPath { get; init; }

    public bool EmitStructuredProgress { get; init; }

    public bool Force { get; init; }

    public bool ShowHelp { get; init; }

    public int? MaxPages { get; init; }

    public bool SummaryOnly { get; init; }

    public string? OllamaEndpoint { get; init; }

    public string? OllamaModel { get; init; }

    public string? OllamaPageAnalysisModel { get; init; }

    public string? OllamaMetadataModel { get; init; }

    public string? OllamaGroupingModel { get; init; }

    public string? OllamaArticleAnalysisModel { get; init; }

    public double? RenderScale { get; init; }

    public int? OllamaTimeout { get; init; }

    public string? TessDataPath { get; init; }

    public string? TesseractLanguage { get; init; }

    public string? Profile { get; init; }

    public double? OcrMlTypeConfidenceThreshold { get; init; }

    public double? OcrMlLengthConfidenceThreshold { get; init; }

    public int? OcrMlMaxPageDeltaThreshold { get; init; }

    public double? OcrMlTypeOverrideConfidenceThreshold { get; init; }
}

internal static class OutputPathResolver
{
    // Always resolves to an output directory.
    // If no path is requested, defaults to a subdirectory named after the PDF in the current directory.
    public static string Resolve(string? requestedPath, string pdfPath, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return Path.Combine(currentDirectory, Path.GetFileNameWithoutExtension(pdfPath));
        }

        return Path.GetFullPath(requestedPath);
    }
}

public sealed class ParseRequest
{
    public required string PdfPath { get; init; }

    public int? MaxPages { get; init; }

    public bool SummaryOnly { get; init; }

    public string? OutputDirectory { get; init; }
}

internal sealed class CliParseState
{
    public string? InputPath { get; set; }

    public string? OutputPath { get; set; }

    public string? ModelRootPath { get; set; }

    public bool EmitStructuredProgress { get; set; }

    public bool Force { get; set; }

    public string? OllamaEndpoint { get; set; }

    public string? OllamaModel { get; set; }

    public string? OllamaPageAnalysisModel { get; set; }

    public string? OllamaMetadataModel { get; set; }

    public string? OllamaGroupingModel { get; set; }

    public string? OllamaArticleAnalysisModel { get; set; }

    public double? RenderScale { get; set; }

    public bool ShowHelp { get; set; }

    public int? MaxPages { get; set; }

    public bool SummaryOnly { get; set; }

    public int? OllamaTimeout { get; set; }

    public string? TessDataPath { get; set; }

    public string? TesseractLanguage { get; set; }

    public string? Profile { get; set; }

    public double? OcrMlTypeConfidenceThreshold { get; set; }

    public double? OcrMlLengthConfidenceThreshold { get; set; }

    public int? OcrMlMaxPageDeltaThreshold { get; set; }

    public double? OcrMlTypeOverrideConfidenceThreshold { get; set; }
}