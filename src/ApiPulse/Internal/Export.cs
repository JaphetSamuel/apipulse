using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPulse.Internal;

/// <summary>Source-generated serializer for the export payload: no reflection, trimming-friendly and allocation-light.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(EventBatch))]
internal sealed partial class ApiPulseJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The export pipeline: a bounded, lock-free queue between request threads (which only ever enqueue) and one background worker that
/// batches events and hands them to the sinks. Requests are never blocked and never wait for the network; when the queue is full
/// new events are dropped and counted.
/// </summary>
internal sealed class ApiPulseExporter : BackgroundService, IApiPulseExporter
{
    private readonly ApiPulseRuntime _runtime;
    private readonly IReadOnlyList<IApiPulseSink> _sinks;
    private readonly ApiPulseMetrics _metrics;
    private readonly ILogger _logger;
    private readonly Channel<RequestEvent> _queue;
    private readonly SemaphoreSlim _batchReady = new(0, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _enqueued, _exported, _dropped, _failed;
    private long _lastFailureLogTicks;

    public ApiPulseExporter(ApiPulseRuntime runtime, IEnumerable<IApiPulseSink> sinks, ApiPulseMetrics metrics, ILoggerFactory loggerFactory)
    {
        _runtime = runtime;
        _sinks = sinks.ToArray();
        _metrics = metrics;
        _logger = loggerFactory.CreateLogger("ApiPulse.Exporter");
        _queue = Channel.CreateBounded<RequestEvent>(new BoundedChannelOptions(runtime.Current.MaxQueueSize)
        {
            // Wait (not DropWrite): with DropWrite, TryWrite reports success even when it discards the item. We only ever call
            // TryWrite, which returns false immediately when the queue is full, so requests never actually wait.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>Whether at least one sink currently accepts events. When false the request path skips event capture.</summary>
    public bool HasActiveSinks
    {
        get
        {
            foreach (var sink in _sinks)
            {
                if (sink.IsEnabled)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool TryEnqueue(RequestEvent evt)
    {
        if (!_queue.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _dropped);
            _metrics.Dropped(1);
            return false;
        }

        Interlocked.Increment(ref _enqueued);
        if (_queue.Reader.Count >= _runtime.Current.BatchSize && _batchReady.CurrentCount == 0)
        {
            try
            {
                _batchReady.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
        }

        return true;
    }

    public ApiPulseStatistics GetStatistics() => new(
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _exported),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _failed),
        _queue.Reader.Count);

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                // Wait for a full batch or for the flush interval, whichever comes first.
                await _batchReady.WaitAsync(_runtime.Current.FlushInterval, stoppingToken).ConfigureAwait(false);
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; StopAsync performs the final flush.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_runtime.Current.ShutdownTimeout);
        try
        {
            await FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ApiPulse could not flush {Count} queued events before shutdown completed.", _queue.Reader.Count);
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var settings = _runtime.Current;
        var batchSize = settings.BatchSize;
        var batch = new List<RequestEvent>(Math.Min(batchSize, 256));

        while (!cancellationToken.IsCancellationRequested)
        {
            batch.Clear();
            while (batch.Count < batchSize && _queue.Reader.TryRead(out var evt))
            {
                batch.Add(evt);
            }

            if (batch.Count == 0)
            {
                return;
            }

            await SendAsync(batch, settings, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(List<RequestEvent> events, ApiPulseSettings settings, CancellationToken cancellationToken)
    {
        var payload = new EventBatch
        {
            Sdk = ApiPulseSettings.Sdk,
            Service = settings.Service,
            SentAt = DateTimeOffset.UtcNow,
            Events = new List<RequestEvent>(events),
        };

        var delivered = false;
        foreach (var sink in _sinks)
        {
            if (!sink.IsEnabled)
            {
                continue;
            }

            try
            {
                await sink.SendAsync(payload, cancellationToken).ConfigureAwait(false);
                delivered = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogFailure(sink, ex, events.Count);
            }
        }

        if (delivered)
        {
            Interlocked.Add(ref _exported, events.Count);
            _metrics.Exported(events.Count);
        }
        else
        {
            Interlocked.Add(ref _failed, events.Count);
            _metrics.Failed(events.Count);
        }
    }

    /// <summary>Logs export failures at most once a minute so an unreachable endpoint cannot flood the log.</summary>
    private void LogFailure(IApiPulseSink sink, Exception exception, int count)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastFailureLogTicks);
        if (last != 0 && now - last < 60_000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastFailureLogTicks, now);
        _logger.LogWarning(exception, "ApiPulse failed to export a batch of {Count} events through {Sink}. Further failures are logged at most once a minute.", count, sink.GetType().Name);
    }

    public override void Dispose()
    {
        _batchReady.Dispose();
        _sendLock.Dispose();
        base.Dispose();
    }
}

/// <summary>Posts batches as JSON to <see cref="ApiPulseOptions.Endpoint"/> with gzip, timeouts and exponential-backoff retries.</summary>
internal sealed class HttpApiPulseSink : IApiPulseSink
{
    private readonly Func<HttpClient> _clientFactory;
    private readonly ApiPulseRuntime _runtime;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public HttpApiPulseSink(IHttpClientFactory factory, ApiPulseRuntime runtime)
        : this(() => factory.CreateClient(ApiPulseDefaults.HttpClientName), runtime, Task.Delay)
    {
    }

    internal HttpApiPulseSink(Func<HttpClient> clientFactory, ApiPulseRuntime runtime, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _clientFactory = clientFactory;
        _runtime = runtime;
        _delay = delay;
    }

    public bool IsEnabled => _runtime.Current is { Options.Enabled: true, Endpoint: not null };

    public async Task SendAsync(EventBatch batch, CancellationToken cancellationToken)
    {
        var settings = _runtime.Current;
        if (settings.Endpoint is not { } endpoint)
        {
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(batch, ApiPulseJsonContext.Default.EventBatch);
        var (body, gzip) = settings.Options.CompressPayloads && json.Length > 1024 ? (Compress(json), true) : (json, false);

        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.ExportTimeout);

            HttpStatusCode? status = null;
            Exception? failure = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (gzip)
                {
                    request.Content.Headers.ContentEncoding.Add("gzip");
                }

                request.Headers.UserAgent.ParseAdd($"ApiPulse.AspNetCore/{ApiPulseSettings.Sdk.Version}");
                if (!string.IsNullOrEmpty(settings.Options.ApiKey))
                {
                    request.Headers.TryAddWithoutValidation("X-Api-Key", settings.Options.ApiKey);
                }

                using var response = await _clientFactory().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                status = response.StatusCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                failure = ex;
            }

            var transient = failure is not null || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
            if (!transient)
            {
                throw new HttpRequestException($"The ApiPulse endpoint rejected the batch with HTTP {(int)status!.Value}; it was dropped.", null, status);
            }

            if (attempt >= settings.MaxRetries)
            {
                throw failure ?? new HttpRequestException($"The ApiPulse endpoint answered HTTP {(int)status!.Value} after {attempt + 1} attempts.", null, status);
            }

            var backoff = TimeSpan.FromMilliseconds((500 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 250));
            await _delay(backoff, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 4);
        using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(data);
        }

        return output.ToArray();
    }
}

/// <summary>Writes a one-line summary of each event to the logger, for development (see <see cref="ApiPulseOptions.LogEvents"/>).</summary>
internal sealed class LoggingApiPulseSink : IApiPulseSink
{
    private readonly ApiPulseRuntime _runtime;
    private readonly ILogger _logger;

    public LoggingApiPulseSink(ApiPulseRuntime runtime, ILoggerFactory loggerFactory)
    {
        _runtime = runtime;
        _logger = loggerFactory.CreateLogger(ApiPulseDefaults.EventsLogCategory);
    }

    public bool IsEnabled => _runtime.Current.Options is { Enabled: true, LogEvents: true };

    public Task SendAsync(EventBatch batch, CancellationToken cancellationToken)
    {
        foreach (var e in batch.Events)
        {
            _logger.LogInformation(
                "{Method} {Route} -> {StatusCode} in {DurationMs:F1} ms (trace {TraceId}, consumer {Consumer}, {LogCount} logs, {ErrorCount} errors, {SpanCount} spans, sampled {Sampled})",
                e.Method,
                e.Route ?? e.Path,
                e.StatusCode,
                e.DurationMs,
                e.TraceId,
                e.Consumer?.Id ?? "-",
                e.Logs?.Count ?? 0,
                e.Exceptions?.Count ?? 0,
                e.Spans?.Count ?? 0,
                e.Sampled);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Starts the span listener and reports, once, how the SDK is configured.</summary>
internal sealed class ApiPulseStartupService : IHostedService
{
    private readonly ApiPulseRuntime _runtime;
    private readonly SpanCollector _spans;
    private readonly ApiPulseExporter _exporter;
    private readonly ILogger _logger;

    public ApiPulseStartupService(ApiPulseRuntime runtime, SpanCollector spans, ApiPulseExporter exporter, ILoggerFactory loggerFactory)
    {
        _runtime = runtime;
        _spans = spans;
        _exporter = exporter;
        _logger = loggerFactory.CreateLogger("ApiPulse.Startup");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _runtime.Current;
        if (!settings.Options.Enabled)
        {
            _logger.LogInformation("ApiPulse is disabled (ApiPulse:Enabled = false).");
            return Task.CompletedTask;
        }

        _spans.Start();

        _logger.LogInformation(
            "ApiPulse monitoring service {Service} ({Environment}); sampling {SamplingRate:P0}, bodies request={RequestBody} response={ResponseBody}, logs {Logs}, endpoint {Endpoint}.",
            settings.Service.Name,
            settings.Service.Environment,
            settings.SamplingRate,
            settings.CaptureRequestBody,
            settings.CaptureResponseBody,
            settings.Options.CaptureLogs ? settings.MinimumLogLevel.ToString() : "off",
            settings.Endpoint?.GetLeftPart(UriPartial.Path) ?? "(none)");

        if (settings.DashboardActive)
        {
            _logger.LogInformation("ApiPulse dashboard available at {DashboardPath}/ (keeping the last {MaxEntries} requests in memory).", settings.DashboardPath, settings.DashboardMaxEntries);
        }
        else if (settings.Options.Dashboard.Enabled && !settings.IsDevelopment)
        {
            _logger.LogInformation("ApiPulse dashboard is off outside Development. Set ApiPulseOptions.Dashboard.Authorize to enable it here.");
        }

        if (!_exporter.HasActiveSinks && !settings.DashboardActive)
        {
            _logger.LogWarning("ApiPulse is enabled but has nowhere to send events: set ApiPulse:Endpoint (or APIPULSE_ENDPOINT), register a custom sink with AddApiPulseSink<T>(), enable the dashboard, or set ApiPulse:LogEvents to true. Request metrics are still published through the 'ApiPulse' meter.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
