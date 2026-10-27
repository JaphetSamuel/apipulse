using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ApiPulse.Internal;

/// <summary>Gives code running inside a request (loggers, spans, <see cref="IApiPulseContext"/>) access to that request's capture.</summary>
internal static class CaptureAccessor
{
    private static readonly AsyncLocal<RequestCapture?> CurrentCapture = new();

    public static RequestCapture? Current
    {
        get => CurrentCapture.Value;
        set => CurrentCapture.Value = value;
    }
}

/// <summary>
/// Mutable, thread-safe state collected while one request is processed. Logs and spans can arrive concurrently from any task
/// that belongs to the request, hence the lock. Everything is bounded so a misbehaving request cannot grow memory without limit.
/// </summary>
internal sealed class RequestCapture
{
    private const int MaxTags = 20;
    private const int MaxTagKeyLength = 64;
    private const int MaxTagValueLength = 256;
    private const int MaxLogProperties = 20;
    private const int MaxExceptions = 10;
    private const int MaxSpanTags = 20;

    private readonly object _gate = new();
    private List<LogEntry>? _logs;
    private List<ExceptionInfo>? _exceptions;
    private List<Exception>? _seenExceptions;
    private List<SpanInfo>? _spans;
    private Dictionary<string, string>? _tags;
    private ApiConsumer? _consumer;

    public RequestCapture(ApiPulseSettings settings, bool sampled, DateTimeOffset startUtc, long startTimestamp)
    {
        Settings = settings;
        Sampled = sampled;
        StartUtc = startUtc;
        StartTimestamp = startTimestamp;
        MinLogLevel = !settings.Options.CaptureLogs
            ? LogLevel.None
            : sampled ? settings.MinimumLogLevel : (LogLevel)Math.Max((int)settings.MinimumLogLevel, (int)LogLevel.Warning);
    }

    public ApiPulseSettings Settings { get; }

    public bool Sampled { get; }

    public DateTimeOffset StartUtc { get; }

    public long StartTimestamp { get; }

    /// <summary>Lowest log level that is recorded. <see cref="LogLevel.None"/> disables log capture.</summary>
    public LogLevel MinLogLevel { get; }

    public string? TraceId { get; set; }

    public bool Keep { get; set; }

    public bool Suppressed { get; set; }

    /// <summary>Set once the request has finished; late log entries from background work are then ignored.</summary>
    public volatile bool Completed;

    public ApiConsumer? Consumer
    {
        get { lock (_gate) { return _consumer; } }
        set { lock (_gate) { _consumer = value; } }
    }

    public bool HasExceptions
    {
        get { lock (_gate) { return _exceptions is { Count: > 0 }; } }
    }

    // ------------------------------------------------------------------ tags

    public void SetTag(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        key = ExceptionInfoFactory.Truncate(key.Trim(), MaxTagKeyLength)!;
        lock (_gate)
        {
            if (value is null)
            {
                _tags?.Remove(key);
                return;
            }

            _tags ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (!_tags.ContainsKey(key) && _tags.Count >= MaxTags)
            {
                return;
            }

            var masked = Settings.Masker.IsSensitiveField(key) ? Settings.Masker.Mask : Settings.Masker.MaskText(value);
            _tags[key] = ExceptionInfoFactory.Truncate(masked, MaxTagValueLength)!;
        }
    }

    // ------------------------------------------------------------------ exceptions

    /// <summary>
    /// Records an exception once per instance, so the same error seen by several layers is reported a single time. If a later layer
    /// reports it as unhandled (it escaped the endpoint and an error handler turned it into a response), that wins over an earlier
    /// "handled" sighting such as the framework's own error log entry.
    /// </summary>
    public void AddException(Exception exception, bool handled)
    {
        lock (_gate)
        {
            _seenExceptions ??= new List<Exception>();
            _exceptions ??= new List<ExceptionInfo>();

            var index = _seenExceptions.FindIndex(e => ReferenceEquals(e, exception));
            if (index >= 0)
            {
                if (!handled)
                {
                    for (var info = _exceptions[index]; info is not null; info = info.Inner)
                    {
                        info.Handled = false;
                    }
                }

                return;
            }

            if (_seenExceptions.Count >= MaxExceptions)
            {
                return;
            }

            _seenExceptions.Add(exception);
            _exceptions.Add(ExceptionInfoFactory.Create(exception, Settings.Masker, handled));
        }
    }

    // ------------------------------------------------------------------ logs

    public void AddLog(
        LogLevel level,
        string category,
        EventId eventId,
        string message,
        Exception? exception,
        IReadOnlyList<KeyValuePair<string, object?>>? properties)
    {
        var masker = Settings.Masker;
        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level.ToString(),
            Category = category,
            EventId = eventId.Id != 0 ? eventId.Id : null,
            EventName = eventId.Name,
            Message = ExceptionInfoFactory.Truncate(masker.MaskText(message), Settings.MaxLogMessageLength)!,
            SpanId = Activity.Current?.SpanId.ToHexString(),
            Properties = BuildProperties(properties),
        };

        lock (_gate)
        {
            _logs ??= new List<LogEntry>();
            if (_logs.Count >= Settings.MaxLogsPerRequest)
            {
                DroppedLogs++;
                return;
            }

            if (exception is not null)
            {
                var escaped = IsFrameworkUnhandledReport(category, eventId);
                entry.Exception = ExceptionInfoFactory.Create(exception, masker, handled: !escaped);
                if (level >= LogLevel.Error)
                {
                    AddException(exception, handled: !escaped);
                }
            }

            _logs.Add(entry);
        }
    }

    public int DroppedLogs { get; private set; }

    /// <summary>
    /// ASP.NET Core's error middleware (exception handler, developer exception page) and Kestrel log an exception that escaped the
    /// endpoint under a well-known event name. Recognising it lets such an exception be reported as unhandled even when the
    /// middleware leaves no <c>IExceptionHandlerFeature</c> behind.
    /// </summary>
    private static bool IsFrameworkUnhandledReport(string category, EventId eventId) =>
        category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
        && eventId.Name is "UnhandledException" or "ApplicationError";

    private Dictionary<string, string?>? BuildProperties(IReadOnlyList<KeyValuePair<string, object?>>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        var masker = Settings.Masker;
        Dictionary<string, string?>? result = null;
        foreach (var (key, value) in properties)
        {
            if (key == "{OriginalFormat}")
            {
                continue;
            }

            result ??= new Dictionary<string, string?>(StringComparer.Ordinal);
            if (result.Count >= MaxLogProperties)
            {
                break;
            }

            var text = masker.IsSensitiveField(key) ? masker.Mask : masker.MaskText(value?.ToString() ?? string.Empty);
            result[key] = ExceptionInfoFactory.Truncate(text, MaxTagValueLength);
        }

        return result;
    }

    // ------------------------------------------------------------------ spans

    public int DroppedSpans { get; private set; }

    public void AddSpan(Activity activity)
    {
        var masker = Settings.Masker;
        var span = new SpanInfo
        {
            SpanId = activity.SpanId.ToHexString(),
            ParentSpanId = activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            Name = activity.DisplayName,
            Source = activity.Source.Name,
            Kind = activity.Kind.ToString(),
            StartOffsetMs = Math.Max(0, (activity.StartTimeUtc - StartUtc.UtcDateTime).TotalMilliseconds),
            DurationMs = activity.Duration.TotalMilliseconds,
            Status = activity.Status.ToString(),
            Tags = BuildSpanTags(activity, masker),
        };

        lock (_gate)
        {
            if (Completed)
            {
                return;
            }

            _spans ??= new List<SpanInfo>();
            if (_spans.Count >= Settings.MaxSpansPerRequest)
            {
                DroppedSpans++;
                return;
            }

            _spans.Add(span);
        }
    }

    /// <summary>
    /// Only standard, low-risk tag families are kept (HTTP, URL, server, database system/name). Statement text and other free-form
    /// tags are deliberately left out because they routinely carry literal user data.
    /// </summary>
    private static Dictionary<string, string?>? BuildSpanTags(Activity activity, Masker masker)
    {
        Dictionary<string, string?>? tags = null;
        foreach (var tag in activity.TagObjects)
        {
            if (!IsSafeTag(tag.Key))
            {
                continue;
            }

            tags ??= new Dictionary<string, string?>(StringComparer.Ordinal);
            if (tags.Count >= MaxSpanTags)
            {
                break;
            }

            var text = tag.Value?.ToString();
            if (text is null)
            {
                continue;
            }

            tags[tag.Key] = masker.IsSensitiveField(tag.Key)
                ? masker.Mask
                : tag.Key is "url.full" or "http.url" or "url.query" ? masker.MaskUrl(text) : masker.MaskText(text);
        }

        return tags;
    }

    private static bool IsSafeTag(string key) =>
        key.StartsWith("http.", StringComparison.Ordinal) || key.StartsWith("url.", StringComparison.Ordinal)
        || key.StartsWith("server.", StringComparison.Ordinal) || key.StartsWith("network.", StringComparison.Ordinal)
        || key.StartsWith("error.", StringComparison.Ordinal)
        || key is "db.system" or "db.system.name" or "db.name" or "db.namespace" or "db.operation" or "db.operation.name";

    // ------------------------------------------------------------------ snapshot

    /// <summary>Copies the collected data into <paramref name="evt"/> and closes the capture to further additions.</summary>
    public void CompleteInto(RequestEvent evt)
    {
        lock (_gate)
        {
            Completed = true;
            evt.Tags = _tags is { Count: > 0 } ? new Dictionary<string, string>(_tags) : null;
            evt.Exceptions = _exceptions is { Count: > 0 } ? new List<ExceptionInfo>(_exceptions) : null;
            evt.Logs = _logs is { Count: > 0 } ? new List<LogEntry>(_logs) : null;
            evt.DroppedLogs = DroppedLogs;
            evt.Spans = _spans is { Count: > 0 } ? new List<SpanInfo>(_spans) : null;
            evt.DroppedSpans = DroppedSpans;
            evt.Consumer = _consumer;
        }
    }
}

/// <summary>Implements <see cref="IApiPulseContext"/> on top of the capture that belongs to the current execution context.</summary>
internal sealed class ApiPulseContext : IApiPulseContext
{
    private readonly RequestCapture? _fixed;

    /// <summary>Resolves the capture from the ambient execution context (used for dependency injection).</summary>
    public ApiPulseContext()
    {
    }

    /// <summary>Binds to one specific capture (used by the <c>HttpContext</c> extensions).</summary>
    public ApiPulseContext(RequestCapture? capture) => _fixed = capture;

    private RequestCapture? Capture => _fixed ?? CaptureAccessor.Current;

    public bool IsActive => Capture is { Completed: false };

    public string? TraceId => Capture?.TraceId;

    public void SetConsumer(string id, string? name = null, string? group = null) =>
        SetConsumer(string.IsNullOrWhiteSpace(id) ? null : new ApiConsumer(id, name, group));

    public void SetConsumer(ApiConsumer? consumer)
    {
        if (Capture is { Completed: false } capture)
        {
            capture.Consumer = consumer;
        }
    }

    public void SetTag(string key, string? value) => Capture?.SetTag(key, value);

    public void RecordException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Capture?.AddException(exception, handled: true);
    }

    public void Keep()
    {
        if (Capture is { } capture)
        {
            capture.Keep = true;
        }
    }

    public void Suppress()
    {
        if (Capture is { } capture)
        {
            capture.Suppressed = true;
        }
    }
}
