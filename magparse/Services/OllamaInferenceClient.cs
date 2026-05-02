using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Collections.Concurrent;
using System.Globalization;
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
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new LenientDecimalJsonConverter(),
            new LenientNullableDecimalJsonConverter(),
            new LenientEnumJsonConverterFactory(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private readonly OllamaApiClient _client;
    private readonly MagparseSettings _settings;
    private readonly HashSet<string> _validatedModels = new(StringComparer.OrdinalIgnoreCase);

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

    public async Task<T> GenerateJsonAsync<T>(string systemPrompt, string prompt, IReadOnlyList<byte[]>? images, string model, CancellationToken cancellationToken)
    {
        await EnsureModelAvailableAsync(model, cancellationToken);

        JsonException? lastJsonException = null;
        string? lastInvalidPayload = null;

        for (var attempt = 1; attempt <= MaxGenerateAttempts; attempt++)
        {
            var effectivePrompt = BuildJsonRetryPrompt(
                prompt,
                attempt,
                lastJsonException,
                lastInvalidPayload);

            var request = new GenerateRequest
            {
                Model = model,
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
                lastInvalidPayload = payload;

                var repairedPayload = await TryRepairJsonPayloadAsync(payload, model, cancellationToken);
                if (TryDeserialize(repairedPayload, out T? repairedResult))
                {
                    return repairedResult!;
                }
            }
        }

        throw new InvalidOperationException($"Ollama returned invalid JSON after {MaxGenerateAttempts} attempts. {lastJsonException?.Message}");
    }

    private static string BuildJsonRetryPrompt(string basePrompt, int attempt, JsonException? lastJsonException, string? lastInvalidPayload)
    {
        if (attempt == 1)
        {
            return basePrompt;
        }

        var errorDetails = lastJsonException is null
            ? "Unknown JSON parse failure."
            : $"{lastJsonException.Message} (Path: {lastJsonException.Path ?? "<none>"}, Line: {lastJsonException.LineNumber?.ToString() ?? "<unknown>"}, Byte: {lastJsonException.BytePositionInLine?.ToString() ?? "<unknown>"})";

        var priorPayload = string.IsNullOrWhiteSpace(lastInvalidPayload)
            ? "<empty>"
            : TrimForPrompt(lastInvalidPayload, 3000);

        return $"""
{basePrompt}

IMPORTANT: Your previous response was malformed JSON and failed strict parsing.
Validation failure:
{errorDetails}

Previous invalid JSON response:
<<<PREVIOUS_INVALID_JSON>>>
{priorPayload}
<<<END_PREVIOUS_INVALID_JSON>>>

Return exactly one valid JSON value that matches the requested schema.
Do not include markdown fences, comments, explanation text, or extra trailing content.
Ensure all braces/brackets are properly closed.
""";
    }

    private static string TrimForPrompt(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "\n\n[TRUNCATED]";
    }

    public async Task<string> GenerateResponseAsync(string systemPrompt, string prompt, IReadOnlyList<byte[]>? images, string model, CancellationToken cancellationToken)
    {
        await EnsureModelAvailableAsync(model, cancellationToken);

        var request = new GenerateRequest
        {
            Model = model,
            System = systemPrompt,
            Prompt = prompt,
            Images = images?.Select(Convert.ToBase64String).ToArray(),
            Stream = false
        };

        return (await ReadResponsePayloadAsync(request, cancellationToken)).Trim();
    }

    private async Task<string?> TryRepairJsonPayloadAsync(string payload, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var repaired = await GenerateResponseAsync(
            "You repair malformed JSON. Return only valid JSON. Do not add commentary.",
            $"The following JSON is malformed or truncated. Repair it so it becomes exactly one valid JSON value. Preserve all existing values when possible. If a closing brace, bracket, null, false, or empty array is required to complete the structure, add the smallest valid fix only.\n\n<<<JSON>>>\n{payload}\n<<<END_JSON>>>",
            null,
            model,
            cancellationToken);

        return CleanJsonPayload(repaired);
    }

    private static bool TryDeserialize<T>(string? payload, out T? result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
            return result is not null;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private async Task EnsureModelAvailableAsync(string model, CancellationToken cancellationToken)
    {
        if (_validatedModels.Contains(model))
        {
            return;
        }

        var localModels = await _client.ListLocalModelsAsync(cancellationToken);
        var exists = localModels.Any(localModel =>
            string.Equals(localModel.Name, model, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(localModel.ModelName, model, StringComparison.OrdinalIgnoreCase));

        if (!exists)
        {
            await foreach (var _ in _client.PullModelAsync(new PullModelRequest
            {
                Model = model,
                Stream = true
            }, cancellationToken))
            {
                // The pull is intentionally silent because stdout is reserved for JSON output.
            }
        }

        _validatedModels.Add(model);
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

    private sealed class LenientEnumJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert.IsEnum;
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var converterType = typeof(LenientEnumJsonConverter<>).MakeGenericType(typeToConvert);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }
    }

    private sealed class LenientEnumJsonConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        private static readonly ConcurrentDictionary<string, TEnum> NormalizedMap = BuildNormalizedMap();

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (Enum.TryParse<TEnum>(raw, ignoreCase: true, out var exactParsed))
                    {
                        return exactParsed;
                    }

                    var normalized = Normalize(raw);
                    if (NormalizedMap.TryGetValue(normalized, out var mapped))
                    {
                        return mapped;
                    }
                }

                return default;
            }

            if (reader.TokenType is JsonTokenType.Number && reader.TryGetInt32(out var intValue))
            {
                var numeric = (TEnum)Enum.ToObject(typeof(TEnum), intValue);
                return Enum.IsDefined(typeof(TEnum), numeric) ? numeric : default;
            }

            return default;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }

        private static ConcurrentDictionary<string, TEnum> BuildNormalizedMap()
        {
            var values = Enum.GetValues<TEnum>();
            var map = new ConcurrentDictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);

            foreach (var value in values)
            {
                var name = value.ToString();
                map.TryAdd(name, value);
                map.TryAdd(Normalize(name), value);
            }

            return map;
        }

        private static string Normalize(string value)
        {
            var chars = value.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }
    }

    private sealed class LenientDecimalJsonConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.Number && reader.TryGetDecimal(out var decimalValue))
            {
                return decimalValue;
            }

            if (reader.TokenType is JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (TryParseLenientDecimal(raw, out var parsed))
                {
                    return parsed;
                }
            }

            return 0m;
        }

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    private sealed class LenientNullableDecimalJsonConverter : JsonConverter<decimal?>
    {
        public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType is JsonTokenType.Number && reader.TryGetDecimal(out var decimalValue))
            {
                return decimalValue;
            }

            if (reader.TokenType is JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (TryParseLenientDecimal(raw, out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteNumberValue(value.Value);
                return;
            }

            writer.WriteNullValue();
        }
    }

    private static bool TryParseLenientDecimal(string? raw, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        // Extract first numeric token from text like "90%", "9/10", "score: 8.5", etc.
        var tokenChars = trimmed
            .Where(ch => char.IsDigit(ch) || ch is '.' or '-' or '+')
            .ToArray();

        if (tokenChars.Length == 0)
        {
            return false;
        }

        var token = new string(tokenChars);
        if (decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        // Handle simple fraction notation such as "9/10" by using the numerator.
        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex > 0)
        {
            var numerator = trimmed[..slashIndex].Trim();
            if (decimal.TryParse(numerator, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        return false;
    }
}