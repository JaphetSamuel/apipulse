using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace ApiPulse.Internal;

/// <summary>
/// Routes finished child activities (outgoing HTTP calls, database calls, custom spans) to the in-flight request that caused them,
/// by walking the activity's parent chain up to a registered request root.
/// </summary>
internal sealed class SpanCollector : IDisposable
{
    private readonly ApiPulseRuntime _runtime;
    private readonly ConcurrentDictionary<string, RequestCapture> _active = new(StringComparer.Ordinal);
    private volatile ActivityListener? _listener;
    private readonly object _startLock = new();

    public SpanCollector(ApiPulseRuntime runtime) => _runtime = runtime;

    public void Register(string rootActivityId, RequestCapture capture) => _active[rootActivityId] = capture;

    public void Unregister(string rootActivityId) => _active.TryRemove(rootActivityId, out _);

    /// <summary>Starts listening. Idempotent. Sources created later are picked up automatically by the listener.</summary>
    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        lock (_startLock)
        {
            if (_listener is not null)
            {
                return;
            }

            var sources = _runtime.Current.SpanSources;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => sources.IsMatch(source.Name),

                // AllData (not AllDataAndRecorded) lets us read tags without influencing the sampling flag that other
                // tracing libraries, such as OpenTelemetry, propagate to downstream services.
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = OnStopped,
            };
            ActivitySource.AddActivityListener(_listener);
        }
    }

    private void OnStopped(Activity activity)
    {
        if (_active.IsEmpty)
        {
            return;
        }

        for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Id is { } id && _active.TryGetValue(id, out var capture))
            {
                try
                {
                    capture.AddSpan(activity);
                }
                catch
                {
                    // Never let telemetry collection disturb the observed application.
                }

                return;
            }
        }
    }

    public void Dispose() => _listener?.Dispose();
}

/// <summary>Request metrics published through <c>System.Diagnostics.Metrics</c>, ready for OpenTelemetry or dotnet-counters.</summary>
internal sealed class ApiPulseMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _exported;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _failed;

    public ApiPulseMetrics(IServiceProvider services)
    {
        var factory = services.GetService(typeof(IMeterFactory)) as IMeterFactory;
        _meter = factory?.Create(ApiPulseDefaults.MeterName) ?? new Meter(ApiPulseDefaults.MeterName);
        _requests = _meter.CreateCounter<long>("apipulse.requests", "{request}", "Requests handled by the monitored application.");
        _duration = _meter.CreateHistogram<double>("apipulse.request.duration", "ms", "Request processing time.");
        _exported = _meter.CreateCounter<long>("apipulse.events.exported", "{event}", "Events delivered to a sink.");
        _dropped = _meter.CreateCounter<long>("apipulse.events.dropped", "{event}", "Events dropped because the export queue was full.");
        _failed = _meter.CreateCounter<long>("apipulse.events.failed", "{event}", "Events lost because delivery failed after all retries.");
    }

    public void RecordRequest(string method, string? route, int statusCode, double durationMs)
    {
        var tags = new TagList
        {
            { "http.request.method", method },
            { "http.route", route ?? "unmatched" },
            { "http.response.status_code", statusCode },
        };
        _requests.Add(1, tags);
        _duration.Record(durationMs, tags);
    }

    public void Exported(int count) => _exported.Add(count);

    public void Dropped(int count) => _dropped.Add(count);

    public void Failed(int count) => _failed.Add(count);

    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// Attaches <c>ILogger</c> output to the request being processed. Registered as an ordinary logger provider, so it coexists
/// with console, file or OpenTelemetry logging. Correlation uses the ambient execution context, which flows through
/// <c>await</c> and into tasks started by the request but never leaks between concurrent requests.
/// </summary>
[ProviderAlias("ApiPulse")]
internal sealed class ApiPulseLoggerProvider : ILoggerProvider
{
    /// <summary>Logger categories used by the SDK itself.</summary>
    internal static readonly HashSet<string> OwnCategories = new(StringComparer.Ordinal)
    {
        "ApiPulse.Configuration", "ApiPulse.Exporter", "ApiPulse.Middleware", "ApiPulse.Startup", ApiPulseDefaults.EventsLogCategory,
    };

    public ILogger CreateLogger(string categoryName) => new ApiPulseLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class ApiPulseLogger : ILogger
    {
        private readonly string _category;
        private readonly bool _ownCategory;

        public ApiPulseLogger(string category)
        {
            _category = category;
            // The SDK's own diagnostics must never be fed back into the events it produces. Matching exact category names
            // (rather than a prefix) keeps logs from an application whose namespace happens to start with "ApiPulse".
            _ownCategory = ApiPulseLoggerProvider.OwnCategories.Contains(category);
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            !_ownCategory
            && logLevel != LogLevel.None
            && CaptureAccessor.Current is { Completed: false } capture
            && logLevel >= capture.MinLogLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || CaptureAccessor.Current is not { } capture)
            {
                return;
            }

            try
            {
                var message = formatter(state, exception);
                var properties = state as IReadOnlyList<KeyValuePair<string, object?>>;
                capture.AddLog(logLevel, _category, eventId, message, exception, properties);
            }
            catch
            {
                // A faulty formatter or ToString() on a log argument must not break the caller's logging.
            }
        }
    }
}
