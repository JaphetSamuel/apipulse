using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiPulse;

/// <summary>How the client IP address is recorded.</summary>
public enum ClientIpMode
{
    /// <summary>Record the full address.</summary>
    Full,

    /// <summary>Zero the host part: the last IPv4 octet, or the last 80 bits of an IPv6 address.</summary>
    Anonymized,

    /// <summary>Do not record the client address at all.</summary>
    None,
}

/// <summary>
/// All settings of the ApiPulse SDK. Every property has a production-ready default, so
/// <c>services.AddApiPulse()</c> with no arguments is a valid setup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration sources and precedence</b> (later wins): defaults, then the code callback passed to
/// <c>AddApiPulse</c>, then the <c>ApiPulse</c> section of any <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// (appsettings.json, <c>ApiPulse__*</c> environment variables, user secrets, ...), then the short
/// <c>APIPULSE_*</c> environment variables. Deployment-time sources deliberately win over code so an operator can,
/// for example, switch the SDK off with <c>APIPULSE_ENABLED=false</c> without a rebuild.
/// </para>
/// <para>
/// <b>List settings are additive</b> when they come from configuration: values from appsettings or the environment
/// are appended to the defaults. To remove a default, call <c>Clear()</c> on the list in the code callback.
/// </para>
/// <para>
/// <b>Short environment variables:</b> <c>APIPULSE_ENABLED</c>, <c>APIPULSE_API_KEY</c>, <c>APIPULSE_ENDPOINT</c>,
/// <c>APIPULSE_SERVICE_NAME</c>, <c>APIPULSE_SERVICE_VERSION</c>, <c>APIPULSE_ENVIRONMENT</c>,
/// <c>APIPULSE_SAMPLING_RATE</c>, <c>APIPULSE_CAPTURE_REQUEST_BODY</c>, <c>APIPULSE_CAPTURE_RESPONSE_BODY</c>,
/// <c>APIPULSE_CAPTURE_LOGS</c>, <c>APIPULSE_MIN_LOG_LEVEL</c>, <c>APIPULSE_LOG_EVENTS</c> and the comma-separated lists
/// <c>APIPULSE_EXCLUDED_PATHS</c>, <c>APIPULSE_MASK_HEADERS</c>, <c>APIPULSE_MASK_QUERY_PARAMS</c>,
/// <c>APIPULSE_MASK_BODY_FIELDS</c>.
/// </para>
/// </remarks>
public sealed class ApiPulseOptions
{
    /// <summary>Name of the configuration section bound to this type (<c>"ApiPulse"</c>).</summary>
    public const string SectionName = "ApiPulse";

    // ---------------------------------------------------------------- general

    /// <summary>
    /// Master switch. When <see langword="false"/> the middleware is a pass-through and nothing is captured.
    /// Checked on every request, so it can be flipped at runtime through configuration reload. Default <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>API key sent to the ingest endpoint in the <c>X-Api-Key</c> header. Optional.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Ingest endpoint that receives batches of events as JSON over HTTP POST.
    /// When <see langword="null"/> and no custom <see cref="IApiPulseSink"/> is registered nothing is exported
    /// (a warning is logged at startup); request metrics are still published through <c>System.Diagnostics.Metrics</c>.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Logical name of this service. Defaults to the host application name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Version of this service. Defaults to the entry assembly's informational version.</summary>
    public string? ServiceVersion { get; set; }

    /// <summary>Deployment environment label (<c>prod</c>, <c>staging</c>...). Defaults to the host environment name.</summary>
    public string? EnvironmentName { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default) the SDK inserts its middleware as the outermost component of the pipeline
    /// through an <c>IStartupFilter</c>, so no <c>app.UseApiPulse()</c> call is needed. Set to <see langword="false"/> and call
    /// <c>app.UseApiPulse()</c> yourself if you need to control the position.
    /// </summary>
    public bool AutoRegisterMiddleware { get; set; } = true;

    /// <summary>
    /// The built-in local dashboard (a Telescope-style UI at <c>/apipulse</c>). Development-only unless
    /// <see cref="DashboardOptions.Authorize"/> is set. It works with no endpoint configured.
    /// </summary>
    public DashboardOptions Dashboard { get; } = new();

    // ---------------------------------------------------------------- capture

    /// <summary>Capture request headers (values are subject to <see cref="MaskHeaders"/>). Default <see langword="true"/>.</summary>
    public bool CaptureRequestHeaders { get; set; } = true;

    /// <summary>Capture response headers (values are subject to <see cref="MaskHeaders"/>). Default <see langword="true"/>.</summary>
    public bool CaptureResponseHeaders { get; set; } = true;

    /// <summary>Capture the query string (values are subject to <see cref="MaskQueryParams"/>). Default <see langword="true"/>.</summary>
    public bool CaptureQueryString { get; set; } = true;

    /// <summary>
    /// Capture request bodies. <see langword="null"/> (the default) means automatic: on in the Development environment while the
    /// local dashboard is active, off everywhere else, because bodies routinely contain personal data. An explicit
    /// <see langword="true"/> or <see langword="false"/> always wins. Masking applies either way. Only the bytes the application
    /// actually reads are recorded, up to <see cref="MaxBodySize"/>.
    /// </summary>
    public bool? CaptureRequestBody { get; set; }

    /// <summary>
    /// Capture response bodies (up to <see cref="MaxBodySize"/>). <see langword="null"/> (the default) means automatic, like
    /// <see cref="CaptureRequestBody"/>.
    /// </summary>
    public bool? CaptureResponseBody { get; set; }

    /// <summary>Maximum number of body bytes recorded per request and per response. Default 32 KiB.</summary>
    public int MaxBodySize { get; set; } = 32 * 1024;

    /// <summary>
    /// Media types whose bodies may be captured. Supports <c>*</c> wildcards (<c>text/*</c>, <c>application/*+json</c>).
    /// Bodies of other types (images, multipart uploads, ...) and compressed bodies are never recorded.
    /// </summary>
    public IList<string> BodyContentTypes { get; } = new List<string>(ApiPulseDefaults.BodyContentTypes);

    /// <summary>How the client IP address is recorded. Default <see cref="ClientIpMode.Full"/>.</summary>
    public ClientIpMode ClientIp { get; set; } = ClientIpMode.Full;

    // ---------------------------------------------------------------- logs

    /// <summary>
    /// Capture <c>ILogger</c> output emitted while a request is being processed and attach it to that request.
    /// Default <see langword="true"/>. Note that the usual <c>Logging:LogLevel</c> filters apply; the provider alias for
    /// per-provider filters is <c>ApiPulse</c>.
    /// </summary>
    public bool CaptureLogs { get; set; } = true;

    /// <summary>Lowest level of log entry attached to sampled requests. Default <see cref="LogLevel.Information"/>.</summary>
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;

    /// <summary>Maximum number of log entries kept per request; the rest are counted as dropped. Default 100.</summary>
    public int MaxLogsPerRequest { get; set; } = 100;

    /// <summary>Maximum length of a single log message. Longer messages are truncated. Default 4096.</summary>
    public int MaxLogMessageLength { get; set; } = 4096;

    // ---------------------------------------------------------------- traces

    /// <summary>
    /// Attach child spans (outgoing HTTP calls, database calls, custom <c>ActivitySource</c> spans...) to the request that
    /// caused them. Default <see langword="true"/>.
    /// </summary>
    public bool CaptureSpans { get; set; } = true;

    /// <summary>
    /// <c>ActivitySource</c> names to collect spans from. Supports <c>*</c> wildcards. The SDK's own source (<c>ApiPulse</c>) is
    /// always included, so custom spans created with <see cref="ApiPulseActivitySource.Instance"/> always show up.
    /// </summary>
    public IList<string> SpanSources { get; } = new List<string>(ApiPulseDefaults.SpanSources);

    /// <summary>Maximum number of spans kept per request. Default 50.</summary>
    public int MaxSpansPerRequest { get; set; } = 50;

    // ---------------------------------------------------------------- filtering

    /// <summary>
    /// Paths that are never monitored (no event, no metrics). Case-insensitive, trailing slashes ignored, <c>*</c> is a wildcard.
    /// Defaults cover health checks, metrics endpoints, OpenAPI/Swagger UIs and the favicon.
    /// </summary>
    public IList<string> ExcludedPaths { get; } = new List<string>(ApiPulseDefaults.ExcludedPaths);

    /// <summary>HTTP methods that are never monitored, e.g. <c>OPTIONS</c> to skip CORS preflights. Empty by default.</summary>
    public IList<string> ExcludedMethods { get; } = new List<string>();

    /// <summary>
    /// Optional predicate evaluated at the start of every request. Return <see langword="false"/> to skip the request.
    /// Endpoint-level exclusion is also available through <see cref="ExcludeFromApiPulseAttribute"/>.
    /// </summary>
    public Func<HttpContext, bool>? Filter { get; set; }

    // ---------------------------------------------------------------- sampling

    /// <summary>
    /// Fraction of requests captured in full (headers, bodies, logs, spans), between 0 and 1. The decision is derived from the
    /// trace id, so all services in a distributed trace agree. Metrics always cover 100% of requests. Default 1.
    /// </summary>
    public double SamplingRate { get; set; } = 1.0;

    /// <summary>
    /// When <see langword="true"/> (default), requests that fail (status 5xx or an exception) are always reported even when they
    /// were not sampled. Such an event carries metadata, exceptions and warning-or-higher logs but no bodies or spans.
    /// </summary>
    public bool AlwaysCaptureErrors { get; set; } = true;

    /// <summary>
    /// When set, unsampled requests that take at least this long are reported like errors (metadata only).
    /// <see langword="null"/> (default) disables this.
    /// </summary>
    public TimeSpan? SlowRequestThreshold { get; set; }

    /// <summary>
    /// Optional per-request sampling override evaluated at the start of the request. Return <see langword="true"/> to capture
    /// in full, <see langword="false"/> to not sample it, or <see langword="null"/> to fall back to <see cref="SamplingRate"/>.
    /// </summary>
    public Func<HttpContext, bool?>? Sampler { get; set; }

    // ---------------------------------------------------------------- masking

    /// <summary>Replacement text for masked values. Default <c>******</c>.</summary>
    public string MaskValue { get; set; } = ApiPulseDefaults.DefaultMask;

    /// <summary>Header names whose values are masked (request and response). Case-insensitive, <c>*</c> wildcards allowed.</summary>
    public IList<string> MaskHeaders { get; } = new List<string>(ApiPulseDefaults.MaskHeaders);

    /// <summary>Query-string parameter names whose values are masked. Case-insensitive, <c>*</c> wildcards allowed.</summary>
    public IList<string> MaskQueryParams { get; } = new List<string>(ApiPulseDefaults.MaskQueryParams);

    /// <summary>
    /// Field names masked in JSON, XML and form-encoded bodies, at any nesting depth, and in structured log properties and span
    /// tags. Case-insensitive, <c>*</c> wildcards allowed.
    /// </summary>
    public IList<string> MaskBodyFields { get; } = new List<string>(ApiPulseDefaults.MaskBodyFields);

    /// <summary>
    /// Regular expressions whose matches are masked wherever free text is recorded: bodies, log messages, exception messages and
    /// header or query values. Compiled in non-backtracking mode (linear time, safe against ReDoS). Defaults mask
    /// <c>Bearer</c> tokens and JWTs. See <see cref="ApiPulseDefaults.CreditCardPattern"/> for a ready-made card-number pattern.
    /// </summary>
    public IList<string> MaskValuePatterns { get; } = new List<string>(ApiPulseDefaults.MaskValuePatterns);

    // ---------------------------------------------------------------- consumers and hooks

    /// <summary>
    /// Identifies who is calling the API. Evaluated once, when the request finishes (so authentication has run). Anything set
    /// explicitly with <see cref="IApiPulseContext.SetConsumer(string, string?, string?)"/> takes precedence.
    /// </summary>
    public Func<HttpContext, ApiConsumer?>? ConsumerResolver { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default) and no consumer was set or resolved, the authenticated user becomes the consumer
    /// (claim <c>sub</c>, then <c>nameidentifier</c>, then the identity name).
    /// </summary>
    public bool AutoDetectConsumer { get; set; } = true;

    /// <summary>
    /// Last-chance hook to inspect, modify or drop an event before it is queued for export. Return the (possibly modified) event,
    /// or <see langword="null"/> to discard it. Exceptions thrown by the hook are swallowed and the event is dropped.
    /// </summary>
    public Func<RequestEvent, RequestEvent?>? BeforeSend { get; set; }

    // ---------------------------------------------------------------- export

    /// <summary>Maximum number of events waiting for export. When full, new events are dropped, never blocking requests. Default 10 000.</summary>
    public int MaxQueueSize { get; set; } = 10_000;

    /// <summary>Number of events sent per batch. Default 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Longest time an event waits in the queue before being exported. Default 5 seconds.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout of a single export attempt. Default 10 seconds.</summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retries after a transient export failure (network error, 408, 429, 5xx), with exponential backoff. Default 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Time allowed to flush queued events when the application shuts down. Default 5 seconds.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Compress export payloads with gzip when larger than 1 KiB. Default <see langword="true"/>.</summary>
    public bool CompressPayloads { get; set; } = true;

    /// <summary>
    /// Write a one-line summary of every exported event to the logger category <c>ApiPulse.Events</c>. Handy in development
    /// to see what is being captured. Default <see langword="false"/>.
    /// </summary>
    public bool LogEvents { get; set; }
}

/// <summary>The built-in default lists and constants used by <see cref="ApiPulseOptions"/>.</summary>
public static class ApiPulseDefaults
{
    /// <summary>Name of the <c>HttpClient</c> used by the built-in HTTP sink; configure it to customise the handler or proxy.</summary>
    public const string HttpClientName = "ApiPulse";

    /// <summary>Name of the <c>System.Diagnostics.Metrics.Meter</c> published by the SDK.</summary>
    public const string MeterName = "ApiPulse";

    /// <summary>Name of the <c>ActivitySource</c> owned by the SDK.</summary>
    public const string ActivitySourceName = "ApiPulse";

    /// <summary>Logger category used by <see cref="ApiPulseOptions.LogEvents"/>.</summary>
    public const string EventsLogCategory = "ApiPulse.Events";

    /// <summary>Default replacement text for masked values.</summary>
    public const string DefaultMask = "******";

    /// <summary>Matches 13 to 19 digit numbers optionally separated by spaces or dashes (payment card numbers). Not enabled by default.</summary>
    public const string CreditCardPattern = @"\b(?:\d[ -]?){12,18}\d\b";

    internal static readonly string[] BodyContentTypes =
    {
        "application/json", "application/*+json", "application/xml", "application/*+xml",
        "text/*", "application/x-www-form-urlencoded",
    };

    internal static readonly string[] SpanSources =
    {
        "System.Net.Http", "OpenTelemetry.Instrumentation.*", "Npgsql", "Microsoft.EntityFrameworkCore",
    };

    internal static readonly string[] ExcludedPaths =
    {
        "/health", "/health/*", "/healthz", "/healthz/*", "/ready", "/readyz", "/live", "/livez", "/alive",
        "/metrics", "/favicon.ico", "/robots.txt", "/swagger/*", "/openapi/*", "/scalar/*",
    };

    internal static readonly string[] MaskHeaders =
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "X-Auth-Token",
        "X-Csrf-Token", "X-Xsrf-Token", "*secret*", "*password*", "*token*", "*api-key*", "*apikey*",
    };

    internal static readonly string[] MaskQueryParams =
    {
        "*token*", "*password*", "*passwd*", "pwd", "*secret*", "*api_key*", "*apikey*", "*api-key*",
        "key", "code", "sig", "signature", "auth*", "session*",
    };

    internal static readonly string[] MaskBodyFields =
    {
        "*password*", "*passwd*", "pwd", "*secret*", "*token*", "*api_key*", "*apikey*", "*api-key*",
        "authorization", "*card*number*", "cardnumber", "cvv", "cvc", "*cvv*", "ssn", "*social*security*",
        "pin", "otp", "*private*key*",
    };

    internal static readonly string[] MaskValuePatterns =
    {
        @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
        @"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*",
    };
}
