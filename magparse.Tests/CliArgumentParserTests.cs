using magparse.Services;
using Xunit;

namespace magparse.Tests;

public sealed class CliArgumentParserTests
{
    [Fact]
    public void TryParse_ParsesAllSupportedOptions()
    {
        var success = CliArgumentParser.TryParse(
            [
                "sample.pdf",
                "--output", "results/",
                "--model-root", "models/",
                "--progress",
                "--force",
                "--max-pages", "12",
                "--summary-only",
                "--ollama-model", "gemma3:12b",
                "--ollama-page-analysis-model", "llava:13b",
                "--ollama-metadata-model", "qwen2.5:7b",
                "--ollama-grouping-model", "qwen2.5:7b",
                "--ollama-article-analysis-model", "mistral:7b",
                "--ollama-timeout", "600",
                "--tessdata-path", "./tessdata",
                "--tesseract-language", "eng"
            ],
            out var options,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.Equal("sample.pdf", options.InputPath);
        Assert.Equal("results/", options.OutputPath);
        Assert.Equal("models/", options.ModelRootPath);
        Assert.True(options.EmitStructuredProgress);
        Assert.True(options.Force);
        Assert.Equal(12, options.MaxPages);
        Assert.True(options.SummaryOnly);
        Assert.Equal("gemma3:12b", options.OllamaModel);
        Assert.Equal("llava:13b", options.OllamaPageAnalysisModel);
        Assert.Equal("qwen2.5:7b", options.OllamaMetadataModel);
        Assert.Equal("qwen2.5:7b", options.OllamaGroupingModel);
        Assert.Equal("mistral:7b", options.OllamaArticleAnalysisModel);
        Assert.Equal(600, options.OllamaTimeout);
        Assert.Equal("./tessdata", options.TessDataPath);
        Assert.Equal("eng", options.TesseractLanguage);
    }

    [Fact]
    public void TryParse_RejectsInvalidMaxPages()
    {
        var success = CliArgumentParser.TryParse(
            ["sample.pdf", "--max-pages", "0"],
            out var options,
            out var errorMessage);

        Assert.False(success);
        Assert.Equal(CliOptions.Empty.InputPath, options.InputPath);
        Assert.Equal("The --max-pages option requires a positive integer.", errorMessage);
    }

    [Fact]
    public void TryParse_AllowsHelpWithoutPdf()
    {
        var success = CliArgumentParser.TryParse(["--help"], out var options, out var errorMessage);

        Assert.True(success);
        Assert.True(options.ShowHelp);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void TryParse_NormalizesHighQualityProfileAlias()
    {
        var success = CliArgumentParser.TryParse(
            ["sample.pdf", "--profile", "HighQuality"],
            out var options,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.Equal("quality", options.Profile);
    }

    [Fact]
    public void TryParse_RejectsUnknownProfile()
    {
        var success = CliArgumentParser.TryParse(
            ["sample.pdf", "--profile", "ultra"],
            out var options,
            out var errorMessage);

        Assert.False(success);
        Assert.Equal(CliOptions.Empty.InputPath, options.InputPath);
        Assert.Equal("The --profile option must be one of: faster, balanced, quality, highquality, or high-quality.", errorMessage);
    }

    [Fact]
    public void TryParse_AcceptsDirectoryInputPath()
    {
        var success = CliArgumentParser.TryParse(
            ["./issues"],
            out var options,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.Equal("./issues", options.InputPath);
    }

    [Fact]
    public void TryParse_ParsesForceFlagWithoutOtherOptions()
    {
        var success = CliArgumentParser.TryParse(
            ["sample.pdf", "--force"],
            out var options,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.True(options.Force);
    }
}