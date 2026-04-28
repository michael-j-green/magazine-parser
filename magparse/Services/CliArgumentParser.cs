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

            if (state.PdfPath is not null)
            {
                options = CliOptions.Empty;
                errorMessage = "Only one PDF path may be supplied.";
                return false;
            }

            state.PdfPath = argument;
            index++;
        }

        if (!state.ShowHelp && string.IsNullOrWhiteSpace(state.PdfPath))
        {
            options = CliOptions.Empty;
            errorMessage = "A PDF path is required.";
            return false;
        }

        options = new CliOptions
        {
            PdfPath = state.PdfPath ?? string.Empty,
            OutputPath = state.OutputPath,
            EmitStructuredProgress = state.EmitStructuredProgress,
            ShowHelp = state.ShowHelp,
            MaxPages = state.MaxPages,
            SummaryOnly = state.SummaryOnly,
            RawOutput = state.RawOutput,
            OllamaEndpoint = state.OllamaEndpoint,
            OllamaModel = state.OllamaModel,
            RenderScale = state.RenderScale,
            OllamaTimeout = state.OllamaTimeout
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
            case "--summary-only":
                state.SummaryOnly = true;
                index++;
                return true;
            case "--raw-output":
                state.RawOutput = true;
                index++;
                return true;
            default:
                return false;
        }
    }

    private static bool TryRequiresValue(string argument)
    {
        return argument is "-o" or "--output" or "--max-pages" or "--ollama-endpoint" or "--ollama-model" or "--render-scale" or "--ollama-timeout";
    }

    private static bool TryHandleValueOption(string[] args, string argument, CliParseState state, ref int index, out string? errorMessage)
    {
        if (!TryReadValue(args, index, argument, out var value, out errorMessage))
        {
            return false;
        }

        if (argument is "-o" or "--output")
        {
            state.OutputPath = value;
            index += 2;
            return true;
        }

        if (argument is "--ollama-endpoint")
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                errorMessage = "The --ollama-endpoint option requires a valid URI.";
                return false;
            }
            state.OllamaEndpoint = value;
            index += 2;
            return true;
        }

        if (argument is "--ollama-model")
        {
            state.OllamaModel = value;
            index += 2;
            return true;
        }

        if (argument is "--render-scale")
        {
            if (!double.TryParse(value, out var parsedScale) || parsedScale <= 0)
            {
                errorMessage = "The --render-scale option requires a positive number.";
                return false;
            }
            state.RenderScale = parsedScale;
            index += 2;
            return true;
        }

        if (argument is "--ollama-timeout")
        {
            if (!int.TryParse(value, out var parsedTimeout) || parsedTimeout <= 0)
            {
                errorMessage = "The --ollama-timeout option requires a positive integer (seconds).";
                return false;
            }
            state.OllamaTimeout = parsedTimeout;
            index += 2;
            return true;
        }

        if (!int.TryParse(value, out var parsedMaxPages) || parsedMaxPages <= 0)
        {
            errorMessage = "The --max-pages option requires a positive integer.";
            return false;
        }

        state.MaxPages = parsedMaxPages;
        index += 2;
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
    public static CliOptions Empty => new() { PdfPath = string.Empty, ShowHelp = false };

    public required string PdfPath { get; init; }

    public string? OutputPath { get; init; }

    public bool EmitStructuredProgress { get; init; }

    public bool ShowHelp { get; init; }

    public int? MaxPages { get; init; }

    public bool SummaryOnly { get; init; }

    public bool RawOutput { get; init; }

    public string? OllamaEndpoint { get; init; }

    public string? OllamaModel { get; init; }

    public double? RenderScale { get; init; }

    public int? OllamaTimeout { get; init; }
}

internal static class OutputPathResolver
{
    public static string Resolve(string? requestedPath, string pdfPath, string currentDirectory)
    {
        var defaultFileName = Path.GetFileNameWithoutExtension(pdfPath) + ".json";

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return Path.Combine(currentDirectory, defaultFileName);
        }

        var fullPath = Path.GetFullPath(requestedPath);
        if (Directory.Exists(fullPath) || EndsWithDirectorySeparator(requestedPath))
        {
            return Path.Combine(fullPath, defaultFileName);
        }

        return fullPath;
    }

    private static bool EndsWithDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);
    }
}

public sealed class ParseRequest
{
    public required string PdfPath { get; init; }

    public int? MaxPages { get; init; }

    public bool SummaryOnly { get; init; }

    public string? RawOutputDirectory { get; init; }
}

internal sealed class CliParseState
{
    public string? PdfPath { get; set; }

    public string? OutputPath { get; set; }

    public bool EmitStructuredProgress { get; set; }

    public bool RawOutput { get; set; }

    public string? OllamaEndpoint { get; set; }

    public string? OllamaModel { get; set; }

    public double? RenderScale { get; set; }

    public bool ShowHelp { get; set; }

    public int? MaxPages { get; set; }

    public bool SummaryOnly { get; set; }

    public int? OllamaTimeout { get; set; }
}