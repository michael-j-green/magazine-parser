using System.Text.Json;
using System.Text.Json.Serialization;
using magparse.Services;

namespace magparse;

internal static class Program
{
    private const string DefaultsToModelEnvironmentVariableText = "                              Defaults to MAGPARSE_OLLAMA_MODEL";

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> Main(string[] args)
    {
        if (!CliArgumentParser.TryParse(args, out var options, out var errorMessage))
        {
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                await Console.Error.WriteLineAsync(errorMessage);
                await Console.Error.WriteLineAsync();
            }

            await PrintUsageAsync();
            return 1;
        }

        if (options.ShowHelp)
        {
            await PrintUsageAsync();
            return 0;
        }

        var pdfPath = Path.GetFullPath(options.PdfPath);
        if (!File.Exists(pdfPath))
        {
            await Console.Error.WriteLineAsync($"The PDF was not found: {pdfPath}");
            return 1;
        }

        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync("magparse only accepts a single PDF file path.");
            return 1;
        }

        try
        {
            var outputDirectory = OutputPathResolver.Resolve(options.OutputPath, pdfPath, Environment.CurrentDirectory);
            var settings = MagparseSettings.FromEnvironment(options);
            var progressReporter = new ConsoleProgressReporter(options.EmitStructuredProgress);

            await progressReporter.ReportAsync(
                "startup",
                $"Starting parse for {Path.GetFileName(pdfPath)}");

            var renderer = new PdfPageRenderer(settings);
            var ollama = new OllamaInferenceClient(settings);
            var ocr = new TesseractOcrClient(settings);
            var parser = new MagazineParserPipeline(settings, renderer, ollama, ocr, progressReporter);

            var magazine = await parser.ParseAsync(new ParseRequest
            {
                PdfPath = pdfPath,
                MaxPages = options.MaxPages,
                SummaryOnly = options.SummaryOnly,
                OutputDirectory = outputDirectory
            }, CancellationToken.None);

            Directory.CreateDirectory(outputDirectory);
            var jsonPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(pdfPath) + ".json");
            var json = JsonSerializer.Serialize(magazine, OutputJsonOptions);

            await progressReporter.ReportAsync(
                "write-output",
                $"Writing JSON output to {jsonPath}");

            await File.WriteAllTextAsync(jsonPath, json);

            await progressReporter.ReportAsync(
                "complete",
                $"Finished parsing {magazine.PageCount} page(s) into {magazine.Articles.Count} article(s). Output written to {outputDirectory}",
                current: 1,
                total: 1,
                percent: 1);

            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task PrintUsageAsync()
    {
        await Console.Error.WriteLineAsync("Usage: magparse <path-to-magazine.pdf> [--output <directory>] [--progress] [--max-pages <number>] [--summary-only] [--profile <faster|balanced|quality>] [--ollama-endpoint <uri>] [--ollama-model <model>] [--ollama-page-analysis-model <model>] [--ollama-metadata-model <model>] [--ollama-grouping-model <model>] [--ollama-article-analysis-model <model>] [--render-scale <number>] [--ollama-timeout <seconds>] [--tessdata-path <path>] [--tesseract-language <id>]");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Options:");
        await Console.Error.WriteLineAsync("  -o, --output <directory>   Output directory for all artefacts (JSON, OCR text, page images).");
        await Console.Error.WriteLineAsync("                              Defaults to ./<input-file-name>/ in the current directory.");
        await Console.Error.WriteLineAsync("  --progress                 Emit machine-readable JSON progress lines to stdout.");
        await Console.Error.WriteLineAsync("  --max-pages <number>       Limit processing to the first N pages.");
        await Console.Error.WriteLineAsync("  --summary-only             Skip article grouping and article analysis.");
        await Console.Error.WriteLineAsync("  --profile <name>           Set all model options to a preset profile: faster, balanced, or quality.");
        await Console.Error.WriteLineAsync("                              Individual --ollama-*-model flags override the profile when both are supplied.");
        await Console.Error.WriteLineAsync("  --ollama-endpoint <uri>    Override Ollama endpoint (e.g., http://127.0.0.1:11434).");
        await Console.Error.WriteLineAsync("  --ollama-model <model>     Set the default Ollama model for all inference stages.");
        await Console.Error.WriteLineAsync("  --ollama-page-analysis-model <model>");
        await Console.Error.WriteLineAsync("                              Override the vision-capable model used for page analysis.");
        await Console.Error.WriteLineAsync("  --ollama-metadata-model <model>");
        await Console.Error.WriteLineAsync("                              Override the model used for magazine metadata inference.");
        await Console.Error.WriteLineAsync("  --ollama-grouping-model <model>");
        await Console.Error.WriteLineAsync("                              Override the model used for article boundary grouping.");
        await Console.Error.WriteLineAsync("  --ollama-article-analysis-model <model>");
        await Console.Error.WriteLineAsync("                              Override the model used for article analysis.");
        await Console.Error.WriteLineAsync("  --render-scale <number>    Override PDF render scale (e.g., 2.5 for ~180 DPI).");
        await Console.Error.WriteLineAsync("  --ollama-timeout <seconds> Override HTTP timeout for Ollama requests (default 900).");
        await Console.Error.WriteLineAsync("  --tessdata-path <path>     Path to tessdata directory containing language files (default ./tessdata).");
        await Console.Error.WriteLineAsync("  --tesseract-language <id>  Tesseract language code (default eng).");
        await Console.Error.WriteLineAsync("  -h, --help                 Show this help text.");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Optional environment variables (overridden by CLI options):");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_ENDPOINT   Defaults to http://127.0.0.1:11434");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_MODEL      Defaults to gemma3:12b");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_PAGE_ANALYSIS_MODEL");
        await Console.Error.WriteLineAsync(DefaultsToModelEnvironmentVariableText);
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_METADATA_MODEL");
        await Console.Error.WriteLineAsync(DefaultsToModelEnvironmentVariableText);
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_GROUPING_MODEL");
        await Console.Error.WriteLineAsync(DefaultsToModelEnvironmentVariableText);
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_ARTICLE_ANALYSIS_MODEL");
        await Console.Error.WriteLineAsync(DefaultsToModelEnvironmentVariableText);
        await Console.Error.WriteLineAsync("  MAGPARSE_RENDER_SCALE      Defaults to 2.5 (roughly 180 DPI)");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_TIMEOUT    Defaults to 900 (seconds, ~15 minutes)");
        await Console.Error.WriteLineAsync("  MAGPARSE_TESSDATA_PATH     Defaults to ./tessdata");
        await Console.Error.WriteLineAsync("  MAGPARSE_TESSERACT_LANGUAGE Defaults to eng");
    }
}
