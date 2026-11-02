using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Http;

namespace ApiPulse.Internal;

/// <summary>
/// Carries the payloads of the outgoing call being made down to the HTTP activity it produces. The capturing handler sits above
/// the runtime's own handler, which is the one that starts the activity, so the two only meet through the ambient execution context.
/// </summary>
internal static class OutgoingScope
{
    private static readonly AsyncLocal<OutgoingExchange?> CurrentExchange = new();

    public static OutgoingExchange? Current
    {
        get => CurrentExchange.Value;
        set => CurrentExchange.Value = value;
    }
}

/// <summary>
/// Headers and bodies of one outgoing HTTP call. Filled in while the call runs and applied to the call's span when the request that
/// made it completes. Bodies may still be arriving at that point (the response can be read after the request finished), hence the locking.
/// </summary>
internal sealed class OutgoingExchange
{
    private readonly object _gate = new();
    private int _linked;
    private Dictionary<string, string>? _requestHeaders;
    private Dictionary<string, string>? _responseHeaders;
    private string? _requestContentType;
    private string? _responseContentType;
    private BodyBuffer? _requestBody;
    private BodyBuffer? _responseBody;

    /// <summary>Claims this exchange for one span. A redirect produces several spans; the first one wins.</summary>
    public bool TryLink() => Interlocked.Exchange(ref _linked, 1) == 0;

    public void SetRequest(Dictionary<string, string>? headers, string? contentType)
    {
        lock (_gate)
        {
            _requestHeaders = headers;
            _requestContentType = contentType;
        }
    }

    /// <summary>Called on every serialization of the request body, so a retry or redirect replaces the previous attempt.</summary>
    public void SetRequestBody(BodyBuffer buffer)
    {
        lock (_gate)
        {
            _requestBody = buffer;
        }
    }

    public void SetResponse(Dictionary<string, string>? headers, string? contentType, BodyBuffer? body)
    {
        lock (_gate)
        {
            _responseHeaders = headers;
            _responseContentType = contentType;
            _responseBody = body;
        }
    }

    public void ApplyTo(SpanInfo span, Masker masker)
    {
        Dictionary<string, string>? requestHeaders, responseHeaders;
        string? requestType, responseType;
        BodyBuffer? requestBody, responseBody;
        lock (_gate)
        {
            requestHeaders = _requestHeaders;
            responseHeaders = _responseHeaders;
            requestType = _requestContentType;
            responseType = _responseContentType;
            requestBody = _requestBody;
            responseBody = _responseBody;
        }

        span.RequestHeaders = requestHeaders;
        span.ResponseHeaders = responseHeaders;

        (span.RequestBody, span.RequestBodyTruncated) = MaskedBody(requestBody, requestType, masker);
        (span.ResponseBody, span.ResponseBodyTruncated) = MaskedBody(responseBody, responseType, masker);
    }

    private static (string? Text, bool Truncated) MaskedBody(BodyBuffer? buffer, string? contentType, Masker masker)
    {
        if (buffer is null)
        {
            return (null, false);
        }

        var bytes = buffer.Snapshot(out var truncated);
        return bytes.Length == 0 ? (null, false) : (masker.MaskBody(bytes, contentType), truncated);
    }
}

/// <summary>
/// Adds <see cref="OutgoingCaptureHandler"/> to every client created by <c>IHttpClientFactory</c>, except the SDK's own. Registered as a
/// builder filter so applications do not have to opt in per client.
/// </summary>
internal sealed class OutgoingCaptureFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);

        // Added after the application's own handlers, so it is the innermost one: it sees the message as it goes on the wire
        // and each attempt of a retry policy gets its own payloads.
        if (builder.Name != ApiPulseDefaults.HttpClientName)
        {
            builder.AdditionalHandlers.Add(new OutgoingCaptureHandler());
        }
    };
}

/// <summary>
/// Records the headers and bodies of outgoing calls made while a request is being monitored. Like the rest of the SDK it only
/// observes: the message is passed on unchanged, nothing is read ahead of the application, and a failure in here never reaches the caller.
/// </summary>
internal sealed class OutgoingCaptureHandler : DelegatingHandler
{
    private const int MaxHeaders = 64;
    private const int MaxHeaderLength = 2048;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApiPulseSettings? settings = null;
        OutgoingExchange? exchange = null;
        HttpContent? original = null;
        var wrapped = false;

        try
        {
            if (CaptureAccessor.Current is { Completed: false, Sampled: true } capture && capture.Settings.Options.CaptureSpans)
            {
                settings = capture.Settings;
                exchange = new OutgoingExchange();
                original = request.Content;
                wrapped = Begin(request, settings, exchange);
            }
        }
        catch
        {
            exchange = null;
        }

        if (exchange is null || settings is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        OutgoingScope.Current = exchange;
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OutgoingScope.Current = null;
            if (wrapped)
            {
                // Put the application's content back so an outer retry handler does not wrap it a second time.
                try
                {
                    request.Content = original;
                }
                catch
                {
                    // The message was disposed under us; there is nothing left to restore.
                }
            }
        }

        try
        {
            Complete(response, settings, exchange);
        }
        catch
        {
            // Never let telemetry collection disturb the observed application.
        }

        return response;
    }

    /// <summary>Snapshots the request headers and, when eligible, wraps the request body. Returns <see langword="true"/> when the body was wrapped.</summary>
    private static bool Begin(HttpRequestMessage request, ApiPulseSettings settings, OutgoingExchange exchange)
    {
        var options = settings.Options;
        var content = request.Content;
        exchange.SetRequest(
            options.CaptureRequestHeaders ? Snapshot(request.Headers, content?.Headers, settings.Masker) : null,
            content?.Headers.ContentType?.ToString());

        if (content is null || !settings.CaptureOutgoingBodies || settings.MaxBodySize == 0 || !IsCapturable(settings, content.Headers))
        {
            return false;
        }

        request.Content = new CapturedRequestContent(content, exchange, settings.MaxBodySize);
        return true;
    }

    private static void Complete(HttpResponseMessage response, ApiPulseSettings settings, OutgoingExchange exchange)
    {
        var options = settings.Options;
        var content = response.Content;
        BodyBuffer? buffer = null;
        if (settings.CaptureOutgoingBodies && settings.MaxBodySize > 0 && IsCapturable(settings, content.Headers))
        {
            buffer = new BodyBuffer(settings.MaxBodySize);
            response.Content = new CapturedResponseContent(content, buffer);
        }

        exchange.SetResponse(
            options.CaptureResponseHeaders ? Snapshot(response.Headers, content.Headers, settings.Masker) : null,
            content.Headers.ContentType?.ToString(),
            buffer);
    }

    private static bool IsCapturable(ApiPulseSettings settings, HttpContentHeaders headers)
    {
        var media = Masker.MediaTypeOf(headers.ContentType?.ToString());
        return media.Length > 0
            && settings.BodyContentTypes.IsMatch(media)
            && headers.ContentEncoding.Count == 0
            && headers.ContentLength != 0;
    }

    private static Dictionary<string, string>? Snapshot(HttpHeaders headers, HttpHeaders? contentHeaders, Masker masker)
    {
        Dictionary<string, string>? result = null;
        Add(headers);
        if (contentHeaders is not null)
        {
            Add(contentHeaders);
        }

        return result;

        void Add(HttpHeaders source)
        {
            foreach (var (name, values) in source)
            {
                result ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (result.Count >= MaxHeaders)
                {
                    return;
                }

                result[name] = ExceptionInfoFactory.Truncate(masker.MaskHeader(name, string.Join(", ", values)), MaxHeaderLength)!;
            }
        }
    }
}

/// <summary>
/// Stands in for the application's request content while it is sent, copying the first bytes aside as they are written to the wire.
/// The original content is serialized exactly as before and stays owned by the caller.
/// </summary>
internal sealed class CapturedRequestContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly OutgoingExchange _exchange;
    private readonly int _limit;

    public CapturedRequestContent(HttpContent inner, OutgoingExchange exchange, int limit)
    {
        _inner = inner;
        _exchange = exchange;
        _limit = limit;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = new BodyBuffer(_limit);
        _exchange.SetRequestBody(buffer);
        await _inner.CopyToAsync(new CapturingWriteStream(stream, buffer, static () => true), context, cancellationToken).ConfigureAwait(false);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        var known = _inner.Headers.ContentLength;
        length = known ?? 0;
        return known.HasValue;
    }
}

/// <summary>
/// Stands in for the response content, copying the first bytes aside as the application reads them. Nothing is read ahead of time,
/// so streaming responses are unaffected. Owns the original content: disposing this disposes it.
/// </summary>
internal sealed class CapturedResponseContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly BodyBuffer _buffer;

    public CapturedResponseContent(HttpContent inner, BodyBuffer buffer)
    {
        _inner = inner;
        _buffer = buffer;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await using var source = new CapturingReadStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _buffer);
        await source.CopyToAsync(stream).ConfigureAwait(false);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync() =>
        new CapturingReadStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _buffer, ownsInner: true);

    protected override bool TryComputeLength(out long length)
    {
        var known = _inner.Headers.ContentLength;
        length = known ?? 0;
        return known.HasValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
