namespace magparse.Services;

public sealed class MagparseSettings
{
    public required Uri OllamaEndpoint { get; init; }

    public required string OllamaModel { get; init; }

    public required double RenderScale { get; init; }

    public required int OllamaTimeoutSeconds { get; init; }

    public static MagparseSettings FromEnvironment(string? cliEndpoint = null, string? cliModel = null, double? cliRenderScale = null, int? cliTimeout = null)
    {
        var endpoint = cliEndpoint ?? Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_ENDPOINT");
        var model = cliModel ?? Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_MODEL");
        var renderScale = cliRenderScale.HasValue ? cliRenderScale.Value.ToString() : Environment.GetEnvironmentVariable("MAGPARSE_RENDER_SCALE");
        var timeout = cliTimeout.HasValue ? cliTimeout.Value.ToString() : Environment.GetEnvironmentVariable("MAGPARSE_OLLAMA_TIMEOUT");

        return new MagparseSettings
        {
            OllamaEndpoint = new Uri(endpoint ?? "http://127.0.0.1:11434", UriKind.Absolute),
            OllamaModel = string.IsNullOrWhiteSpace(model) ? "gemma3:12b" : model.Trim(),
            RenderScale = double.TryParse(renderScale, out var parsedScale) && parsedScale > 0 ? parsedScale : 2.5,
            OllamaTimeoutSeconds = int.TryParse(timeout, out var parsedTimeout) && parsedTimeout > 0 ? parsedTimeout : 900
        };
    }
}