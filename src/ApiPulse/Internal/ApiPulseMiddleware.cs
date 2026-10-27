using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace ApiPulse.Internal;

/// <summary>
/// Observes every request. Designed to sit at the very top of the pipeline so it sees the whole request, including work done by
/// other middleware. The contract with the application is strict: it never alters behaviour, never swallows an exception, never
/// waits for the network, and never throws from its own code.
/// </summary>
internal sealed class ApiPulseMiddleware
{
    internal const string ItemKey = "ApiPulse.Capture";

    private static readonly object NotCaptured = new();

    private readonly RequestDelegate _next;
    private readonly ApiPulseRuntime _runtime;
    private readonly ApiPulseExporter _exporter;
    private readonly EventStore _store;
    private readonly SpanCollector _spans;
    private readonly ApiPulseMetrics _metrics;
    private readonly ILogger _logger;
    private long _lastInternalErrorTicks;

    public ApiPulseMiddleware(
        RequestDelegate next,
        ApiPulseRuntime runtime,
        ApiPulseExporter exporter,
        EventStore store,
        SpanCollector spans,
        ApiPulseMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        _next = next;
        _runtime = runtime;
        _exporter = exporter;
        _store = store;
        _spans = spans;
        _metrics = metrics;
        _logger = loggerFactory.CreateLogger("ApiPulse.Middleware");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Decide whether to observe this request. Only this decision is guarded: the application itself is invoked outside
        // the try block, so an application exception can never be mistaken for an SDK failure and re-run.
        ApiPulseSettings? settings = null;
        try
        {
            var current = _runtime.Current;
            if (current.Options.Enabled && !context.Items.ContainsKey(ItemKey) && !IsExcluded(current, context))
            {
                settings = current;
            }
        }
        catch (Exception ex)
        {
            ReportInternalError(ex);
        }

        if (settings is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var flow = Begin(context, settings);
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (Observe(flow, context, ex))
        {
            throw; // Unreachable: Observe always returns false so the exception is never intercepted.
        }
        finally
        {
            await EndAsync(flow, context).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ begin

    /// <summary>Everything that has to be undone or read once the request finishes.</summary>
    private sealed class Flow
    {
        public required ApiPulseSettings Settings { get; init; }

        public required long StartTimestamp { get; init; }

        public required DateTimeOffset StartUtc { get; init; }

        public RequestCapture? Capture { get; set; }

        public Activity? Activity { get; set; }

        public Activity? OwnedActivity { get; set; }

        public string? ActivityId { get; set; }

        public Exception? Failure { get; set; }

        public bool Aborted { get; set; }

        public bool ExcludedEndpoint { get; set; }

        public string Path { get; set; } = "";

        public string? RawQuery { get; set; }

        public Stream? OriginalRequestBody { get; set; }

        public BodyBuffer? RequestBuffer { get; set; }

        public string? RequestContentType { get; set; }

        public Stream? OriginalResponseBody { get; set; }

        public BodyBuffer? ResponseBuffer { get; set; }

        public Func<Task>? CompleteResponse { get; set; }
    }

    private Flow Begin(HttpContext context, ApiPulseSettings settings)
    {
        var flow = new Flow
        {
            Settings = settings,
            StartTimestamp = Stopwatch.GetTimestamp(),
            StartUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            var request = context.Request;
            flow.Path = request.PathBase.Add(request.Path).Value ?? "/";
            flow.RawQuery = request.QueryString.Value;

            _spans.Start(); // Idempotent; covers the SDK being enabled by a configuration reload after startup.

            var activity = Activity.Current;
            if (activity is null)
            {
                activity = flow.OwnedActivity = ApiPulseActivitySource.StartRequestActivity(context);
            }

            flow.Activity = activity;
            flow.ActivityId = activity?.Id;

            if (!_exporter.HasActiveSinks && !_store.IsActive)
            {
                // Nowhere to send events: keep only the cheap metrics.
                context.Items[ItemKey] = NotCaptured;
                return flow;
            }

            var sampled = IsSampled(settings, context, activity);
            var capture = new RequestCapture(settings, sampled, flow.StartUtc, flow.StartTimestamp)
            {
                TraceId = activity?.TraceId.ToHexString(),
            };

            flow.Capture = capture;
            context.Items[ItemKey] = capture;
            CaptureAccessor.Current = capture;

            if (!sampled)
            {
                return flow;
            }

            if (settings.Options.CaptureSpans && flow.ActivityId is not null)
            {
                _spans.Register(flow.ActivityId, capture);
            }

            WrapBodies(context, settings, flow);
        }
        catch (Exception ex)
        {
            ReportInternalError(ex);
        }

        context.Items.TryAdd(ItemKey, NotCaptured);
        return flow;
    }

    private static bool IsExcluded(ApiPulseSettings settings, HttpContext context)
    {
        var request = context.Request;
        if (settings.ExcludedMethods.Count > 0 && settings.ExcludedMethods.Contains(request.Method))
        {
            return true;
        }

        if (settings.ExcludedPaths.IsMatch(request.Path.Value))
        {
            return true;
        }

        return settings.Options.Filter is { } filter && !filter(context);
    }

    private static bool IsSampled(ApiPulseSettings settings, HttpContext context, Activity? activity)
    {
        if (settings.Options.Sampler is { } sampler)
        {
            try
            {
                if (sampler(context) is { } decision)
                {
                    return decision;
                }
            }
            catch
            {
                // A faulty sampler falls back to the configured rate.
            }
        }

        var rate = settings.SamplingRate;
        if (rate >= 1.0)
        {
            return true;
        }

        if (rate <= 0.0)
        {
            return false;
        }

        // Derive the decision from the trace id so every service in a distributed trace makes the same choice.
        if (activity is not null && activity.TraceId != default)
        {
            var hex = activity.TraceId.ToHexString().AsSpan(0, 15);
            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var bits))
            {
                return bits / (double)(1UL << 60) < rate;
            }
        }

        return Random.Shared.NextDouble() < rate;
    }

    private void WrapBodies(HttpContext context, ApiPulseSettings settings, Flow flow)
    {
        var request = context.Request;
        var options = settings.Options;
        if (settings.MaxBodySize == 0 || context.WebSockets.IsWebSocketRequest)
        {
            return;
        }

        if (settings.CaptureRequestBody
            && request.ContentLength != 0
            && !request.Headers.ContainsKey("Content-Encoding")
            && IsCapturableType(settings, request.ContentType))
        {
            flow.OriginalRequestBody = request.Body;
            flow.RequestBuffer = new BodyBuffer(settings.MaxBodySize);
            flow.RequestContentType = request.ContentType;
            request.Body = new CapturingReadStream(request.Body, flow.RequestBuffer);
        }

        if (settings.CaptureResponseBody)
        {
            var response = context.Response;
            flow.OriginalResponseBody = response.Body;
            flow.ResponseBuffer = new BodyBuffer(settings.MaxBodySize);
            response.Body = new CapturingWriteStream(
                response.Body,
                flow.ResponseBuffer,
                () => IsCapturableResponse(settings, response));

            var bodyFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            flow.CompleteResponse = bodyFeature is null ? null : () => bodyFeature.CompleteAsync();
        }
    }

    private static bool IsCapturableType(ApiPulseSettings settings, string? contentType)
    {
        var media = Masker.MediaTypeOf(contentType);
        return media.Length > 0 && settings.BodyContentTypes.IsMatch(media);
    }

    private static bool IsCapturableResponse(ApiPulseSettings settings, HttpResponse response) =>
        IsCapturableType(settings, response.ContentType)
        && StringValues.IsNullOrEmpty(response.Headers.ContentEncoding);

    // ------------------------------------------------------------------ observe

    /// <summary>Exception filter: records the failure without intercepting it. Always returns <see langword="false"/>.</summary>
    private static bool Observe(Flow flow, HttpContext context, Exception exception)
    {
        try
        {
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                flow.Aborted = true;
            }
            else
            {
                flow.Failure = exception;
                flow.Capture?.AddException(exception, handled: false);
            }
        }
        catch
        {
            // Observation must never alter how the exception propagates.
        }

        return false;
    }

    // ------------------------------------------------------------------ end

    private async Task EndAsync(Flow flow, HttpContext context)
    {
        try
        {
            await RestoreBodiesAsync(flow, context).ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(flow.StartTimestamp);
            var evt = BuildEvent(flow, context, elapsed);
            if (flow.ExcludedEndpoint)
            {
                return; // [ExcludeFromApiPulse]: no event and no metrics.
            }

            _metrics.RecordRequest(context.Request.Method, evt.Route, evt.StatusCode, elapsed.TotalMilliseconds);

            if (flow.Capture is { } capture)
            {
                Publish(flow, capture, evt, elapsed);
            }
        }
        catch (Exception ex)
        {
            ReportInternalError(ex);
        }
        finally
        {
            try
            {
                if (flow.ActivityId is not null)
                {
                    _spans.Unregister(flow.ActivityId);
                }

                if (flow.Capture is { } c)
                {
                    c.Completed = true;
                }

                CaptureAccessor.Current = null;
                flow.OwnedActivity?.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static async Task RestoreBodiesAsync(Flow flow, HttpContext context)
    {
        try
        {
            if (flow.CompleteResponse is { } complete && !context.RequestAborted.IsCancellationRequested)
            {
                await complete().ConfigureAwait(false);
            }
        }
        catch
        {
            // The response may already be aborted or completed; nothing more to flush.
        }

        if (flow.OriginalRequestBody is not null)
        {
            context.Request.Body = flow.OriginalRequestBody;
        }

        if (flow.OriginalResponseBody is not null)
        {
            context.Response.Body = flow.OriginalResponseBody;
        }
    }

    private RequestEvent BuildEvent(Flow flow, HttpContext context, TimeSpan elapsed)
    {
        var settings = flow.Settings;
        var options = settings.Options;
        var masker = settings.Masker;
        var request = context.Request;
        var response = context.Response;

        // Error-handling middleware stores the exception it handled and clears the endpoint, so read both from its feature.
        var handler = context.Features.Get<IExceptionHandlerFeature>();
        if (handler?.Error is { } escaped && flow.Failure is null)
        {
            // It escaped the endpoint, so it is unhandled as far as application code is concerned, even though the error
            // handler turned it into a response.
            flow.Capture?.AddException(escaped, handled: false);
            flow.Failure = escaped;
        }

        var endpoint = context.GetEndpoint() ?? handler?.Endpoint;
        flow.ExcludedEndpoint = endpoint?.Metadata.GetMetadata<ExcludeFromApiPulseAttribute>() is not null;

        var status = response.StatusCode;
        if (flow.Failure is not null && !response.HasStarted && status < 400)
        {
            status = StatusCodes.Status500InternalServerError;
        }
        else if (flow.Aborted && !response.HasStarted)
        {
            status = 499;
        }

        var evt = new RequestEvent
        {
            Timestamp = flow.StartUtc,
            TraceId = flow.Capture?.TraceId ?? flow.Activity?.TraceId.ToHexString(),
            SpanId = flow.Activity?.SpanId.ToHexString(),
            ParentSpanId = flow.Activity is { ParentSpanId: var parent } && parent != default ? parent.ToHexString() : null,
            Method = request.Method,
            Scheme = request.Scheme,
            Host = request.Host.Value,
            Path = flow.Path,
            Route = RouteOf(endpoint),
            Endpoint = endpoint?.DisplayName,
            Query = options.CaptureQueryString ? masker.MaskQuery(flow.RawQuery) : null,
            Protocol = request.Protocol,
            StatusCode = status,
            DurationMs = Math.Round(elapsed.TotalMilliseconds, 3),
            ClientIp = ClientIpFormatter.Format(context.Connection.RemoteIpAddress, options.ClientIp),
            Aborted = flow.Aborted,
            Sampled = flow.Capture?.Sampled ?? false,
            RequestSize = flow.RequestBuffer?.Total ?? request.ContentLength,
            ResponseSize = flow.ResponseBuffer?.Total ?? response.ContentLength,
        };

        if (flow.Capture is { Sampled: true })
        {
            if (options.CaptureRequestHeaders)
            {
                evt.RequestHeaders = MaskHeaders(request.Headers, masker);
            }

            if (options.CaptureResponseHeaders)
            {
                evt.ResponseHeaders = MaskHeaders(response.Headers, masker);
            }

            if (flow.RequestBuffer is { HasData: true } requestBuffer)
            {
                evt.RequestBody = masker.MaskBody(requestBuffer.Captured, flow.RequestContentType);
                evt.RequestBodyTruncated = requestBuffer.Truncated;
            }

            if (flow.ResponseBuffer is { HasData: true } responseBuffer && IsCapturableResponse(settings, response))
            {
                evt.ResponseBody = masker.MaskBody(responseBuffer.Captured, response.ContentType);
                evt.ResponseBodyTruncated = responseBuffer.Truncated;
            }
        }

        flow.Capture?.CompleteInto(evt);
        evt.Consumer ??= ResolveConsumer(options, context);
        return evt;
    }

    private void Publish(Flow flow, RequestCapture capture, RequestEvent evt, TimeSpan elapsed)
    {
        var options = flow.Settings.Options;
        if (capture.Suppressed)
        {
            return;
        }

        var failed = evt.StatusCode >= 500 || flow.Failure is not null || capture.HasExceptions;
        var slow = options.SlowRequestThreshold is { } threshold && elapsed >= threshold;
        var promoted = capture.Keep || (options.AlwaysCaptureErrors && failed) || slow;
        if (!capture.Sampled && !promoted)
        {
            return;
        }

        var outgoing = evt;
        if (options.BeforeSend is { } hook)
        {
            try
            {
                outgoing = hook(evt);
            }
            catch (Exception ex)
            {
                ReportInternalError(ex);
                return;
            }
        }

        if (outgoing is null)
        {
            return;
        }

        // The dashboard gets the event immediately; export is batched in the background.
        if (_store.IsActive)
        {
            _store.Add(outgoing);
        }

        if (_exporter.HasActiveSinks)
        {
            _exporter.TryEnqueue(outgoing);
        }
    }

    private static string? RouteOf(Endpoint? endpoint)
    {
        if (endpoint is not RouteEndpoint { RoutePattern.RawText: { Length: > 0 } raw })
        {
            return null;
        }

        return raw[0] == '/' ? raw : "/" + raw;
    }

    private static Dictionary<string, string> MaskHeaders(IHeaderDictionary headers, Masker masker)
    {
        var result = new Dictionary<string, string>(Math.Min(headers.Count, 64), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            if (result.Count >= 64)
            {
                break;
            }

            result[name] = ExceptionInfoFactory.Truncate(masker.MaskHeader(name, values.ToString()), 2048)!;
        }

        return result;
    }

    private ApiConsumer? ResolveConsumer(ApiPulseOptions options, HttpContext context)
    {
        try
        {
            if (options.ConsumerResolver?.Invoke(context) is { } resolved)
            {
                return resolved;
            }

            if (options.AutoDetectConsumer && context.User.Identity is { IsAuthenticated: true } identity)
            {
                var user = context.User;
                var id = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? identity.Name;
                return id is null ? null : new ApiConsumer(id, identity.Name is { } name && name != id ? name : null);
            }
        }
        catch (Exception ex)
        {
            ReportInternalError(ex);
        }

        return null;
    }

    /// <summary>Reports a failure of the SDK itself, at most once a minute, and never to the caller.</summary>
    private void ReportInternalError(Exception exception)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastInternalErrorTicks);
        if (last != 0 && now - last < 60_000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastInternalErrorTicks, now);
        _logger.LogWarning(exception, "ApiPulse hit an internal error and skipped monitoring for a request. This does not affect the application. Further errors are logged at most once a minute.");
    }
}
