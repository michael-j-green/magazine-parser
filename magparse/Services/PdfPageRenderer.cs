using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using magparse.Models;

namespace magparse.Services;

public sealed class PdfPageRenderer
{
    private const int RenderProgressInterval = 5;

    private readonly MagparseSettings _settings;

    public PdfPageRenderer(MagparseSettings settings)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<RenderedPage>> RenderAllPagesAsync(ParseRequest request, IProgressReporter progressReporter, CancellationToken cancellationToken)
    {
        var renderedPages = new List<RenderedPage>();

        // Docnet ships native PDFium assets for the common desktop platforms.
        using var docReader = DocLib.Instance.GetDocReader(request.PdfPath, new PageDimensions(_settings.RenderScale));
        var totalPageCount = docReader.GetPageCount();
        var pageCount = request.MaxPages.HasValue ? Math.Min(totalPageCount, request.MaxPages.Value) : totalPageCount;
        var renderSummary = pageCount == totalPageCount
            ? $"Rendering {pageCount} page(s) from {Path.GetFileName(request.PdfPath)}"
            : $"Rendering {pageCount} of {totalPageCount} page(s) from {Path.GetFileName(request.PdfPath)} due to --max-pages";

        await progressReporter.ReportAsync(
            "render",
            renderSummary,
            current: 0,
            total: pageCount,
            cancellationToken: cancellationToken);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            using var pageReader = docReader.GetPageReader(pageIndex);
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            var imageBytes = pageReader.GetImage();

            using var image = Image.LoadPixelData<Bgra32>(imageBytes, width, height);
            using var stream = new MemoryStream();

            // JPEG keeps the payload small enough for local multimodal prompts.
            await image.SaveAsJpegAsync(stream, new JpegEncoder { Quality = 80 }, cancellationToken);

            renderedPages.Add(new RenderedPage
            {
                PageNumber = pageIndex + 1,
                Width = width,
                Height = height,
                JpegBytes = stream.ToArray()
            });

            if (ShouldReportProgress(pageIndex + 1, pageCount, RenderProgressInterval))
            {
                await progressReporter.ReportAsync(
                    "render",
                    $"Rendered page {pageIndex + 1} of {pageCount}",
                    current: pageIndex + 1,
                    total: pageCount,
                    cancellationToken: cancellationToken);
            }
        }

        return renderedPages;
    }

    private static bool ShouldReportProgress(int current, int total, int interval)
    {
        return current == 1 || current == total || current % interval == 0;
    }
}