namespace ApiPulse;

/// <summary>Identifies the caller of an API request.</summary>
/// <param name="Id">Stable identifier of the consumer (user id, client id, API key id...). Never put secrets here.</param>
/// <param name="Name">Optional human-readable name.</param>
/// <param name="Group">Optional group, tenant or plan the consumer belongs to.</param>
public sealed record ApiConsumer(string Id, string? Name = null, string? Group = null);

/// <summary>Everything the SDK knows about one HTTP request. This is the unit that is exported to the ingest endpoint.</summary>
public sealed class RequestEvent
{
    /// <summary>Unique id of the event.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>UTC time at which the request started.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>W3C trace id of the request. Shared with every log entry and span of the request.</summary>
    public string? TraceId { get; set; }

    /// <summary>Span id of the request's server span.</summary>
    public string? SpanId { get; set; }

    /// <summary>Span id of the caller when the request carried a <c>traceparent</c> header.</summary>
    public string? ParentSpanId { get; set; }

    /// <summary>HTTP method, upper-case.</summary>
    public string Method { get; set; } = "";

    /// <summary>Request scheme (<c>http</c> or <c>https</c>).</summary>
    public string? Scheme { get; set; }

    /// <summary>Value of the <c>Host</c> header.</summary>
    public string? Host { get; set; }

    /// <summary>Raw request path including the path base. Not masked; use <see cref="Route"/> for low-cardinality grouping.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Route template that matched, e.g. <c>/api/v1/orders/{id}</c>. <see langword="null"/> when no endpoint matched.
    /// This is what analytics should group by.
    /// </summary>
    public string? Route { get; set; }

    /// <summary>Display name of the matched endpoint (action or delegate name).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Query string with sensitive parameters masked, including the leading <c>?</c>.</summary>
    public string? Query { get; set; }

    /// <summary>Protocol, e.g. <c>HTTP/2</c>.</summary>
    public string? Protocol { get; set; }

    /// <summary>HTTP status code of the response. 499 means the client disconnected before a response was sent.</summary>
    public int StatusCode { get; set; }

    /// <summary>Total processing time in milliseconds.</summary>
    public double DurationMs { get; set; }

    /// <summary>Size of the request body in bytes, when known.</summary>
    public long? RequestSize { get; set; }

    /// <summary>Size of the response body in bytes, when known.</summary>
    public long? ResponseSize { get; set; }

    /// <summary>Request headers with sensitive values masked.</summary>
    public Dictionary<string, string>? RequestHeaders { get; set; }

    /// <summary>Response headers with sensitive values masked.</summary>
    public Dictionary<string, string>? ResponseHeaders { get; set; }

    /// <summary>Captured request body (text, masked), if body capture is enabled and the content type is eligible.</summary>
    public string? RequestBody { get; set; }

    /// <summary><see langword="true"/> when <see cref="RequestBody"/> was cut at the configured maximum size.</summary>
    public bool RequestBodyTruncated { get; set; }

    /// <summary>Captured response body (text, masked), if body capture is enabled and the content type is eligible.</summary>
    public string? ResponseBody { get; set; }

    /// <summary><see langword="true"/> when <see cref="ResponseBody"/> was cut at the configured maximum size.</summary>
    public bool ResponseBodyTruncated { get; set; }

    /// <summary>Client address, formatted according to <see cref="ApiPulseOptions.ClientIp"/>.</summary>
    public string? ClientIp { get; set; }

    /// <summary>The caller, if identified.</summary>
    public ApiConsumer? Consumer { get; set; }

    /// <summary>Custom key/value context attached with <see cref="IApiPulseContext.SetTag"/>.</summary>
    public Dictionary<string, string>? Tags { get; set; }

    /// <summary>Exceptions raised by the request, handled or not.</summary>
    public List<ExceptionInfo>? Exceptions { get; set; }

    /// <summary><c>ILogger</c> entries emitted during the request.</summary>
    public List<LogEntry>? Logs { get; set; }

    /// <summary>Number of log entries discarded because of <see cref="ApiPulseOptions.MaxLogsPerRequest"/>.</summary>
    public int DroppedLogs { get; set; }

    /// <summary>Child spans (outgoing calls and custom spans) of the request.</summary>
    public List<SpanInfo>? Spans { get; set; }

    /// <summary>Number of spans discarded because of <see cref="ApiPulseOptions.MaxSpansPerRequest"/>.</summary>
    public int DroppedSpans { get; set; }

    /// <summary>
    /// <see langword="true"/> when the request was selected for full capture. <see langword="false"/> means the event was kept
    /// only because it was an error or slow request and therefore has no bodies or spans.
    /// </summary>
    public bool Sampled { get; set; }

    /// <summary><see langword="true"/> when the client disconnected before the request completed.</summary>
    public bool Aborted { get; set; }
}

/// <summary>A captured exception.</summary>
public sealed class ExceptionInfo
{
    /// <summary>Full type name.</summary>
    public string Type { get; set; } = "";

    /// <summary>Exception message with value patterns masked.</summary>
    public string? Message { get; set; }

    /// <summary>Stack trace, truncated to 16 KiB.</summary>
    public string? StackTrace { get; set; }

    /// <summary>Stable hash of the exception type and its top stack frames, for grouping identical errors.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary><see langword="true"/> when the exception did not escape the application (error handler, explicit capture, logged).</summary>
    public bool Handled { get; set; }

    /// <summary>The inner exception, if any (up to 3 levels).</summary>
    public ExceptionInfo? Inner { get; set; }
}

/// <summary>An <c>ILogger</c> entry correlated with a request.</summary>
public sealed class LogEntry
{
    /// <summary>UTC time of the entry.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Log level name (<c>Information</c>, <c>Warning</c>...).</summary>
    public string Level { get; set; } = "";

    /// <summary>Logger category, usually the type name.</summary>
    public string Category { get; set; } = "";

    /// <summary>Numeric event id, when not zero.</summary>
    public int? EventId { get; set; }

    /// <summary>Event name, when set.</summary>
    public string? EventName { get; set; }

    /// <summary>Formatted message with value patterns masked.</summary>
    public string Message { get; set; } = "";

    /// <summary>Structured properties of the message template (sensitive names masked), when available.</summary>
    public Dictionary<string, string?>? Properties { get; set; }

    /// <summary>The attached exception, if any.</summary>
    public ExceptionInfo? Exception { get; set; }

    /// <summary>Id of the span that was active when the entry was written.</summary>
    public string? SpanId { get; set; }
}

/// <summary>A child span of a request, such as an outgoing HTTP call.</summary>
public sealed class SpanInfo
{
    /// <summary>Span id.</summary>
    public string SpanId { get; set; } = "";

    /// <summary>Id of the parent span.</summary>
    public string? ParentSpanId { get; set; }

    /// <summary>Operation name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Name of the <c>ActivitySource</c> that produced the span.</summary>
    public string? Source { get; set; }

    /// <summary>Span kind (<c>Client</c>, <c>Internal</c>...).</summary>
    public string Kind { get; set; } = "";

    /// <summary>Offset of the span start from the start of the request, in milliseconds.</summary>
    public double StartOffsetMs { get; set; }

    /// <summary>Duration in milliseconds.</summary>
    public double DurationMs { get; set; }

    /// <summary><c>Ok</c>, <c>Error</c> or <c>Unset</c>.</summary>
    public string Status { get; set; } = "Unset";

    /// <summary>Selected tags (standard HTTP, URL and database tags). Sensitive names are masked.</summary>
    public Dictionary<string, string?>? Tags { get; set; }
}

/// <summary>Identity of the SDK that produced a batch.</summary>
public sealed class SdkInfo
{
    /// <summary>SDK name.</summary>
    public string Name { get; set; } = "ApiPulse.AspNetCore";

    /// <summary>SDK version.</summary>
    public string Version { get; set; } = "";

    /// <summary>.NET runtime description.</summary>
    public string Runtime { get; set; } = "";
}

/// <summary>Identity of the monitored service.</summary>
public sealed class ServiceInfo
{
    /// <summary>Service name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Service version.</summary>
    public string? Version { get; set; }

    /// <summary>Deployment environment.</summary>
    public string? Environment { get; set; }

    /// <summary>Instance identifier (machine name).</summary>
    public string? Instance { get; set; }
}

/// <summary>The payload sent to <see cref="IApiPulseSink"/>: a batch of events plus the identity of the sender.</summary>
public sealed class EventBatch
{
    /// <summary>The SDK that produced the batch.</summary>
    public SdkInfo Sdk { get; set; } = new();

    /// <summary>The monitored service.</summary>
    public ServiceInfo Service { get; set; } = new();

    /// <summary>UTC time at which the batch was sent.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>The events.</summary>
    public List<RequestEvent> Events { get; set; } = new();
}
