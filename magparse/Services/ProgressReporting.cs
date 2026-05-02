using System.Collections.Concurrent;
using System.Diagnostics;
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

    // Tracks when each stage's counter first reached current=1, keyed by stage name.
    private readonly ConcurrentDictionary<string, (Stopwatch Stopwatch, int LastCurrent)> _stageTimings = new();

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

        // Update stage timing tracking.
        string? avgSuffix = null;
        if (current.HasValue && current.Value > 0)
        {
            var timing = _stageTimings.AddOrUpdate(
                stage,
                _ => (Stopwatch.StartNew(), current.Value),
                (_, existing) => (existing.Stopwatch, current.Value));

            var elapsed = timing.Stopwatch.Elapsed;
            if (elapsed.TotalSeconds > 0 && timing.LastCurrent > 0)
            {
                var avgSeconds = elapsed.TotalSeconds / timing.LastCurrent;
                avgSuffix = avgSeconds >= 60
                    ? $" — avg {avgSeconds / 60:F1}m/item, elapsed {FormatElapsed(elapsed)}"
                    : $" — avg {avgSeconds:F1}s/item, elapsed {FormatElapsed(elapsed)}";
            }
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
            await Console.Error.WriteLineAsync(FormatHumanMessage(update, avgSuffix));

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

    private static string FormatHumanMessage(ProgressUpdate update, string? avgSuffix)
    {
        var progressSuffix = string.Empty;
        if (update.Current.HasValue && update.Total.HasValue)
        {
            progressSuffix = $" {update.Current.Value}/{update.Total.Value}";
        }

        var percentSuffix = update.Percent.HasValue
            ? $" ({update.Percent.Value:P0})"
            : string.Empty;

        return $"[{update.Stage}]{progressSuffix}{percentSuffix} {update.Message}{avgSuffix}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s"
            : elapsed.TotalMinutes >= 1
            ? $"{elapsed.Minutes}m {elapsed.Seconds:D2}s"
            : $"{elapsed.Seconds}s";
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