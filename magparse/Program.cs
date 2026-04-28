using System.Text.Json;
using System.Text.Json.Serialization;
using magparse.Services;

namespace magparse;

internal static class Program
{
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
            var outputPath = OutputPathResolver.Resolve(options.OutputPath, pdfPath, Environment.CurrentDirectory);
            var settings = MagparseSettings.FromEnvironment(options.OllamaEndpoint, options.OllamaModel, options.RenderScale);
            var progressReporter = new ConsoleProgressReporter(options.EmitStructuredProgress);

            await progressReporter.ReportAsync(
                "startup",
                $"Starting parse for {Path.GetFileName(pdfPath)}");

            var renderer = new PdfPageRenderer(settings);
            var ollama = new OllamaInferenceClient(settings);
            var parser = new MagazineParserPipeline(renderer, ollama, progressReporter);

            var outputDirectory = Path.GetDirectoryName(OutputPathResolver.Resolve(options.OutputPath, pdfPath, Environment.CurrentDirectory));

            var magazine = await parser.ParseAsync(new ParseRequest
            {
                PdfPath = pdfPath,
                MaxPages = options.MaxPages,
                SummaryOnly = options.SummaryOnly,
                RawOutputDirectory = options.RawOutput ? outputDirectory : null
            }, CancellationToken.None);
            var json = JsonSerializer.Serialize(magazine, OutputJsonOptions);

            await progressReporter.ReportAsync(
                "write-output",
                $"Writing JSON output to {outputPath}");

            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await File.WriteAllTextAsync(outputPath, json);

            await progressReporter.ReportAsync(
                "complete",
                $"Finished parsing {magazine.PageCount} page(s) into {magazine.Articles.Count} article(s). Output written to {outputPath}",
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
        await Console.Error.WriteLineAsync("Usage: magparse <path-to-magazine.pdf> [--output <path>] [--progress] [--max-pages <number>] [--summary-only] [--raw-output] [--ollama-endpoint <uri>] [--ollama-model <model>] [--render-scale <number>]");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Options:");
        await Console.Error.WriteLineAsync("  -o, --output <path>        Write JSON to a file or directory.");
        await Console.Error.WriteLineAsync("                              Defaults to ./<input-file-name>.json");
        await Console.Error.WriteLineAsync("  --progress                 Emit machine-readable JSON progress lines to stdout.");
        await Console.Error.WriteLineAsync("  --max-pages <number>       Limit processing to the first N pages.");
        await Console.Error.WriteLineAsync("  --summary-only             Skip article grouping and article analysis.");
        await Console.Error.WriteLineAsync("  --raw-output               Write OCR'd text to <filename>_raw-ocr.txt in the same directory as the JSON output.");
        await Console.Error.WriteLineAsync("  --ollama-endpoint <uri>    Override Ollama endpoint (e.g., http://127.0.0.1:11434).");
        await Console.Error.WriteLineAsync("  --ollama-model <model>     Override Ollama model (e.g., gemma3:12b).");
        await Console.Error.WriteLineAsync("  --render-scale <number>    Override PDF render scale (e.g., 2.5 for ~180 DPI).");
        await Console.Error.WriteLineAsync("  -h, --help                 Show this help text.");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Optional environment variables (overridden by CLI options):");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_ENDPOINT   Defaults to http://127.0.0.1:11434");
        await Console.Error.WriteLineAsync("  MAGPARSE_OLLAMA_MODEL      Defaults to gemma3:12b");
        await Console.Error.WriteLineAsync("  MAGPARSE_RENDER_SCALE      Defaults to 2.5 (roughly 180 DPI)");
    }
}
