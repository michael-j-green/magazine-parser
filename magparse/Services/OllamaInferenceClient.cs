using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using OllamaSharp;
using OllamaSharp.Models;

namespace magparse.Services;

public sealed class OllamaInferenceClient
{
    private const int MaxGenerateAttempts = 3;
    private const int MaxTimeoutAttempts = 3;

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
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client = new OllamaApiClient(httpClient);
        _client.SelectedModel = settings.OllamaModel;
    }

    public async Task<T> GenerateJsonAsync<T>(string systemPrompt, string prompt, IReadOnlyList<byte[]>? images, CancellationToken cancellationToken)
    {
        await EnsureModelAvailableAsync(cancellationToken);

        JsonException? lastJsonException = null;

        for (var attempt = 1; attempt <= MaxGenerateAttempts; attempt++)
        {
            var effectivePrompt = attempt == 1
                ? prompt
                : $"{prompt}\n\nIMPORTANT: Your previous response was invalid or incomplete JSON. Return exactly one complete JSON value matching the requested shape. Do not include commentary, markdown fences, or truncated output.";

            var request = new GenerateRequest
            {
                Model = _settings.OllamaModel,
                System = systemPrompt,
                Prompt = effectivePrompt,
                Images = images?.Select(Convert.ToBase64String).ToArray(),
                Format = "json",
                Stream = false
            };

            var responsePayload = await ReadResponsePayloadAsync(request, cancellationToken);
            var payload = CleanJsonPayload(responsePayload);

            try
            {
                var result = JsonSerializer.Deserialize<T>(payload, JsonOptions);

                if (result is null)
                {
                    throw new InvalidOperationException("Ollama returned JSON that could not be deserialized into the requested model.");
                }

                return result;
            }
            catch (JsonException exception) when (attempt < MaxGenerateAttempts)
            {
                lastJsonException = exception;
            }
        }

        throw new InvalidOperationException($"Ollama returned invalid JSON after {MaxGenerateAttempts} attempts. {lastJsonException?.Message}");
    }

    private async Task<string> ReadResponsePayloadAsync(GenerateRequest request, CancellationToken cancellationToken)
    {
        var baseTimeoutSeconds = Math.Max(1, _settings.OllamaTimeoutSeconds);
        Exception? lastTimeoutException = null;

        for (var attempt = 1; attempt <= MaxTimeoutAttempts; attempt++)
        {
            var multiplier = 1 << (attempt - 1);
            var effectiveTimeoutSecondsLong = (long)baseTimeoutSeconds * multiplier;
            var effectiveTimeoutSeconds = effectiveTimeoutSecondsLong > int.MaxValue
                ? int.MaxValue
                : (int)effectiveTimeoutSecondsLong;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));

            try
            {
                return await ReadResponsePayloadOnceAsync(request, timeoutCts.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && attempt < MaxTimeoutAttempts)
            {
                lastTimeoutException = exception;
            }
        }

        throw new InvalidOperationException(
            $"Ollama request timed out after {MaxTimeoutAttempts} attempts (timeouts: {baseTimeoutSeconds}s, {baseTimeoutSeconds * 2}s, {baseTimeoutSeconds * 4}s).",
            lastTimeoutException);
    }

    private async Task<string> ReadResponsePayloadOnceAsync(GenerateRequest request, CancellationToken cancellationToken)
    {
        var fullResponse = new StringBuilder(capacity: 4096);
        GenerateResponseStream? lastChunk = null;

        await foreach (var chunk in _client.GenerateAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            if (chunk is null)
            {
                continue;
            }

            lastChunk = chunk;

            if (!string.IsNullOrEmpty(chunk.Response))
            {
                fullResponse.Append(chunk.Response);
            }
        }

        if (fullResponse.Length > 0)
        {
            return fullResponse.ToString();
        }

        if (!string.IsNullOrWhiteSpace(lastChunk?.Response))
        {
            return lastChunk.Response;
        }

        throw new InvalidOperationException("Ollama returned no response content.");
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
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return ExtractJsonCandidate(trimmed) ?? trimmed;
        }

        return ExtractJsonCandidate(trimmed) ?? trimmed;
    }

    private static string? ExtractJsonCandidate(string payload)
    {
        var objectStart = payload.IndexOf('{');
        var arrayStart = payload.IndexOf('[');
        var start = objectStart switch
        {
            -1 when arrayStart >= 0 => arrayStart,
            >= 0 when arrayStart == -1 => objectStart,
            >= 0 when arrayStart >= 0 => Math.Min(objectStart, arrayStart),
            _ => -1
        };

        if (start < 0)
        {
            return null;
        }

        var openingChar = payload[start];
        var closingChar = openingChar == '{' ? '}' : ']';
        var end = payload.LastIndexOf(closingChar);

        return end > start ? payload[start..(end + 1)] : null;
    }
}