using magparse.Services;
using Xunit;

namespace magparse.Tests;

public sealed class CliArgumentParserTests
{
    [Fact]
    public void TryParse_ParsesAllSupportedOptions()
    {
        var success = CliArgumentParser.TryParse(
            ["sample.pdf", "--output", "results/", "--progress", "--max-pages", "12", "--summary-only"],
            out var options,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.Equal("sample.pdf", options.PdfPath);
        Assert.Equal("results/", options.OutputPath);
        Assert.True(options.EmitStructuredProgress);
        Assert.Equal(12, options.MaxPages);
        Assert.True(options.SummaryOnly);
    }

    [Fact]
    public void TryParse_RejectsInvalidMaxPages()
    {
        var success = CliArgumentParser.TryParse(
            ["sample.pdf", "--max-pages", "0"],
            out var options,
            out var errorMessage);

        Assert.False(success);
        Assert.Equal(CliOptions.Empty.PdfPath, options.PdfPath);
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
}