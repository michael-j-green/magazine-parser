using magparse.Models;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Pix;

namespace magparse.Services;

public sealed class TesseractOcrClient
{
    private readonly MagparseSettings _settings;

    public TesseractOcrClient(MagparseSettings settings)
    {
        _settings = settings;
    }

    public async Task<string> ReadTextAsync(RenderedPage page, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Map language code to Language enum (use English as default)
                var language = MapLanguageCode(_settings.TesseractLanguage);

                using var engine = new Engine(_settings.TessDataPath, language, EngineMode.Default);
                using var img = Image.LoadFromMemory(page.JpegBytes);
                using var page_result = engine.Process(img);

                return page_result.Text.Trim();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Tesseract OCR failed for page {page.PageNumber}. Ensure tessdata path is set correctly via --tessdata-path / MAGPARSE_TESSDATA_PATH.",
                    exception);
            }
        }, cancellationToken);
    }

    private static Language MapLanguageCode(string languageCode)
    {
        // Map common language codes to Language enum values
        // Default to English if not recognized
        return languageCode.ToLowerInvariant() switch
        {
            "eng" => Language.English,
            "fra" => Language.French,
            "deu" => Language.German,
            "ita" => Language.Italian,
            "por" => Language.Portuguese,
            "rus" => Language.Russian,
            "jpn" => Language.Japanese,
            "kor" => Language.Korean,
            _ => Language.English
        };
    }
}
