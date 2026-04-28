using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaSharp;
using OllamaSharp.Models;

namespace magparse.Services;

public sealed class OllamaInferenceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly OllamaApiClient _client;
    private readonly MagparseSettings _settings;
    private bool _modelValidated;

    public OllamaInferenceClient(MagparseSettings settings)
    {
        _settings = settings;
        var httpClient = new HttpClient
        {
            BaseAddress = settings.OllamaEndpoint,
            Timeout = TimeSpan.FromSeconds(200)
        };
        _client = new OllamaApiClient(httpClient);
        _client.SelectedModel = settings.OllamaModel;
    }

    public async Task<T> GenerateJsonAsync<T>(string systemPrompt, string prompt, IReadOnlyList<byte[]>? images, CancellationToken cancellationToken)
    {
        await EnsureModelAvailableAsync(cancellationToken);

        var request = new GenerateRequest
        {
            Model = _settings.OllamaModel,
            System = systemPrompt,
            Prompt = prompt,
            Images = images?.Select(Convert.ToBase64String).ToArray(),
            Format = "json",
            Stream = false
        };

        var response = await _client.GenerateAsync(request, cancellationToken).StreamToEndAsync()
            ?? throw new InvalidOperationException("Ollama returned no generation response.");
        var payload = CleanJsonPayload(response.Response ?? throw new InvalidOperationException("Ollama returned an empty response payload."));
        var result = JsonSerializer.Deserialize<T>(payload, JsonOptions);

        if (result is null)
        {
            throw new InvalidOperationException("Ollama returned JSON that could not be deserialized into the requested model.");
        }

        return result;
    }

    private async Task EnsureModelAvailableAsync(CancellationToken cancellationToken)
    {
        if (_modelValidated)
        {
            return;
        }

        var localModels = await _client.ListLocalModelsAsync(cancellationToken);
        var exists = localModels.Any(model =>
            string.Equals(model.Name, _settings.OllamaModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model.ModelName, _settings.OllamaModel, StringComparison.OrdinalIgnoreCase));

        if (!exists)
        {
            await foreach (var _ in _client.PullModelAsync(new PullModelRequest
            {
                Model = _settings.OllamaModel,
                Stream = true
            }, cancellationToken))
            {
                // The pull is intentionally silent because stdout is reserved for JSON output.
            }
        }

        _modelValidated = true;
    }

    private static string CleanJsonPayload(string payload)
    {
        var trimmed = payload.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');

            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed[firstBrace..(lastBrace + 1)];
            }
        }

        return trimmed;
    }
}