using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agent.Core.Telemetry;

namespace Agent.Tests.Support;

internal sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// Subscribes to the instruments of one <see cref="AgentTelemetry"/> instance only (compared by reference), so
/// parallel tests that each own a telemetry object cannot see each other's measurements.
/// </summary>
internal sealed class MetricsCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<RecordedMeasurement> _measurements = [];
    private readonly Lock _gate = new();

    public MetricsCollector(AgentTelemetry telemetry)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, telemetry.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<RecordedMeasurement> All
    {
        get
        {
            lock (_gate)
            {
                return _measurements.ToList();
            }
        }
    }

    public IReadOnlyList<RecordedMeasurement> For(string instrument) => All.Where(m => m.Instrument == instrument).ToList();

    public static string? Tag(RecordedMeasurement measurement, string key) =>
        measurement.Tags.TryGetValue(key, out var value) ? value as string : null;

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
        lock (_gate)
        {
            _measurements.Add(new RecordedMeasurement(instrument.Name, value, copy));
        }
    }
}

/// <summary>Collects finished spans of one <see cref="AgentTelemetry"/> activity source.</summary>
internal sealed class SpanCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _spans = [];
    private readonly Lock _gate = new();

    public SpanCollector(AgentTelemetry telemetry)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.ActivitySource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> All
    {
        get
        {
            lock (_gate)
            {
                return _spans.ToList();
            }
        }
    }

    public static string? Tag(Activity span, string key) => span.TagObjects.FirstOrDefault(t => t.Key == key).Value?.ToString();

    public void Dispose() => _listener.Dispose();
}
