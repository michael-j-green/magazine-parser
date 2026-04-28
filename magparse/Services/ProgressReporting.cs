using System.Text.Json;

namespace magparse.Services;

public interface IProgressReporter
{
    Task ReportAsync(
        string stage,
        string message,
        int? current = null,
        int? total = null,
        double? percent = null,
        CancellationToken cancellationToken = default);
}

public sealed class ConsoleProgressReporter : IProgressReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly bool _emitStructuredProgress;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ConsoleProgressReporter(bool emitStructuredProgress)
    {
        _emitStructuredProgress = emitStructuredProgress;
    }

    public async Task ReportAsync(
        string stage,
        string message,
        int? current = null,
        int? total = null,
        double? percent = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPercent = percent;
        if (!normalizedPercent.HasValue && current.HasValue && total is > 0)
        {
            normalizedPercent = (double)current.Value / total.Value;
        }

        var update = new ProgressUpdate
        {
            Timestamp = DateTimeOffset.UtcNow,
            Stage = stage,
            Message = message,
            Current = current,
            Total = total,
            Percent = normalizedPercent
        };

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // Human-readable status always goes to stderr.
            await Console.Error.WriteLineAsync(FormatHumanMessage(update));

            // Structured progress is opt-in so orchestration can parse stdout safely.
            if (_emitStructuredProgress)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(update, JsonOptions));
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string FormatHumanMessage(ProgressUpdate update)
    {
        var progressSuffix = string.Empty;
        if (update.Current.HasValue && update.Total.HasValue)
        {
            progressSuffix = $" {update.Current.Value}/{update.Total.Value}";
        }

        var percentSuffix = update.Percent.HasValue
            ? $" ({update.Percent.Value:P0})"
            : string.Empty;

        return $"[{update.Stage}]{progressSuffix}{percentSuffix} {update.Message}";
    }
}

public sealed class ProgressUpdate
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Stage { get; init; }

    public required string Message { get; init; }

    public int? Current { get; init; }

    public int? Total { get; init; }

    public double? Percent { get; init; }
}