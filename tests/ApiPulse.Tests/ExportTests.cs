using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ApiPulse.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiPulse.Tests;

public class ExporterTests
{
    private sealed class ThrowingSink : IApiPulseSink
    {
        public bool IsEnabled => true;

        public Task SendAsync(EventBatch batch, CancellationToken cancellationToken) => throw new IOException("sink down");
    }

    private sealed class BlockingSink : IApiPulseSink
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsEnabled => true;

        public Task SendAsync(EventBatch batch, CancellationToken cancellationToken) => Release.Task;
    }

    [Fact]
    public async Task Events_are_exported_in_batches_of_the_configured_size()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.BatchSize = 2; o.FlushInterval = TimeSpan.FromHours(1); },
        });

        for (var i = 0; i < 5; i++)
        {
            await app.Client.GetAsync("/search");
        }

        await app.Exporter.FlushAsync();

        Assert.Equal(new[] { 2, 2, 1 }, app.Sink.Batches.Select(b => b.Events.Count).OrderByDescending(x => x).ToArray());
        var stats = app.Exporter.GetStatistics();
        Assert.Equal(5, stats.Enqueued);
        Assert.Equal(5, stats.Exported);
        Assert.Equal(0, stats.Queued);
    }

    [Fact]
    public async Task A_full_batch_is_sent_without_waiting_for_the_flush_interval()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.BatchSize = 3; o.FlushInterval = TimeSpan.FromHours(1); },
        });

        for (var i = 0; i < 3; i++)
        {
            await app.Client.GetAsync("/search");
        }

        await Eventually(() => app.Sink.Events.Count == 3);
    }

    [Fact]
    public async Task Events_are_flushed_on_the_flush_interval()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.FlushInterval = TimeSpan.FromMilliseconds(100),
        });

        await app.Client.GetAsync("/search");

        await Eventually(() => app.Sink.Events.Count == 1);
    }

    [Fact]
    public async Task Queued_events_are_flushed_when_the_host_shuts_down()
    {
        var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.FlushInterval = TimeSpan.FromHours(1),
        });
        await app.Client.GetAsync("/search");
        Assert.Empty(app.Sink.Events);

        await app.App.StopAsync();

        Assert.Single(app.Sink.Events);
        await app.App.DisposeAsync();
    }

    [Fact]
    public async Task A_full_queue_drops_new_events_instead_of_blocking_requests()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.MaxQueueSize = 3; o.BatchSize = 1000; o.FlushInterval = TimeSpan.FromHours(1); },
        });

        for (var i = 0; i < 10; i++)
        {
            var response = await app.Client.GetAsync("/search");
            Assert.True(response.IsSuccessStatusCode);
        }

        var stats = app.Exporter.GetStatistics();
        Assert.Equal(3, stats.Enqueued);
        Assert.Equal(7, stats.Dropped);
        Assert.Equal(3, stats.Queued);
        await app.Exporter.FlushAsync();
        Assert.Equal(3, app.Sink.Events.Count);
    }

    [Fact]
    public async Task A_slow_sink_never_delays_requests()
    {
        var blocking = new BlockingSink();
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.BatchSize = 1; o.FlushInterval = TimeSpan.FromMilliseconds(10); },
            Services = s => s.AddSingleton<IApiPulseSink>(blocking),
        });

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            await app.Client.GetAsync("/search");
        }

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        blocking.Release.SetResult();
    }

    [Fact]
    public async Task A_failing_sink_is_contained_and_other_sinks_still_receive_events()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Services = s => s.AddApiPulseSink<ThrowingSink>(),
        });

        var response = await app.Client.GetAsync("/search");
        await app.Exporter.FlushAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Single(app.Sink.Events);
        Assert.Equal(1, app.Exporter.GetStatistics().Exported);
    }

    [Fact]
    public async Task Events_lost_because_every_sink_failed_are_counted()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            WithSink = false,
            Services = s => s.AddApiPulseSink<ThrowingSink>(),
        });

        await app.Client.GetAsync("/search");
        await app.Exporter.FlushAsync();

        Assert.Equal(1, app.Exporter.GetStatistics().Failed);
    }

    [Fact]
    public async Task LogEvents_writes_a_summary_line_per_event_for_development()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            WithSink = false,
            Configure = o => o.LogEvents = true,
        });

        await app.Client.GetAsync("/orders/3");
        await app.Exporter.FlushAsync();

        var line = Assert.Single(app.AppLogs.Entries, x => x.Category == ApiPulseDefaults.EventsLogCategory);
        Assert.Contains("GET /orders/{id:int} -> 200", line.Message);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(20);
        }
    }
}

public class HttpSinkTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, byte[], Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Calls.Add((request, body));
            return await respond(request, body);
        }
    }

    private static async Task<(HttpApiPulseSink Sink, List<TimeSpan> Delays)> CreateAsync(FakeHandler handler, Action<ApiPulseOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddApiPulse(o =>
        {
            o.Endpoint = new Uri("https://ingest.example.com/v1/events");
            o.ApiKey = "key-123";
            configure?.Invoke(o);
        });
        var host = builder.Build();
        await Task.CompletedTask;

        var delays = new List<TimeSpan>();
        var runtime = host.Services.GetRequiredService<ApiPulseRuntime>();
        var sink = new HttpApiPulseSink(() => new HttpClient(handler), runtime, (d, _) => { delays.Add(d); return Task.CompletedTask; });
        return (sink, delays);
    }

    private static EventBatch Batch(int count = 1, string? route = "/orders/{id}") => new()
    {
        Sdk = new SdkInfo { Version = "1.0.0", Runtime = ".NET" },
        Service = new ServiceInfo { Name = "bookstore", Environment = "prod" },
        SentAt = DateTimeOffset.UtcNow,
        Events = Enumerable.Range(0, count).Select(i => new RequestEvent
        {
            Method = "GET",
            Path = $"/orders/{i}",
            Route = route,
            StatusCode = 200,
            DurationMs = 12.5,
            Timestamp = DateTimeOffset.UtcNow,
        }).ToList(),
    };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    [Fact]
    public async Task Posts_camel_case_json_with_api_key_and_omits_nulls()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Status(HttpStatusCode.Accepted)));
        var (sink, _) = await CreateAsync(handler);

        await sink.SendAsync(Batch(route: null), CancellationToken.None);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ingest.example.com/v1/events", request.RequestUri!.ToString());
        Assert.Equal("key-123", request.Headers.GetValues("X-Api-Key").Single());
        Assert.StartsWith("ApiPulse.AspNetCore/", request.Headers.UserAgent.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("bookstore", doc.RootElement.GetProperty("service").GetProperty("name").GetString());
        var evt = doc.RootElement.GetProperty("events")[0];
        Assert.Equal("GET", evt.GetProperty("method").GetString());
        Assert.Equal(12.5, evt.GetProperty("durationMs").GetDouble());
        Assert.False(evt.TryGetProperty("route", out _));
        Assert.False(evt.TryGetProperty("requestBody", out _));
    }

    [Fact]
    public async Task Large_payloads_are_gzip_compressed_and_small_ones_are_not()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Status(HttpStatusCode.OK)));
        var (sink, _) = await CreateAsync(handler);

        await sink.SendAsync(Batch(count: 100), CancellationToken.None);
        await sink.SendAsync(Batch(count: 1), CancellationToken.None);

        var (bigRequest, bigBody) = handler.Calls[0];
        Assert.Contains("gzip", bigRequest.Content!.Headers.ContentEncoding);
        using var gz = new GZipStream(new MemoryStream(bigBody), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal(100, doc.RootElement.GetProperty("events").GetArrayLength());

        Assert.Empty(handler.Calls[1].Request.Content!.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Compression_can_be_disabled()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Status(HttpStatusCode.OK)));
        var (sink, _) = await CreateAsync(handler, o => o.CompressPayloads = false);

        await sink.SendAsync(Batch(count: 100), CancellationToken.None);

        Assert.Empty(handler.Calls[0].Request.Content!.Headers.ContentEncoding);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Transient_failures_are_retried_with_growing_backoff_until_success(HttpStatusCode failure)
    {
        var attempts = 0;
        var handler = new FakeHandler((_, _) => Task.FromResult(Status(++attempts < 3 ? failure : HttpStatusCode.OK)));
        var (sink, delays) = await CreateAsync(handler);

        await sink.SendAsync(Batch(), CancellationToken.None);

        Assert.Equal(3, handler.Calls.Count);
        Assert.Equal(2, delays.Count);
        Assert.True(delays[1] > delays[0]);
    }

    [Fact]
    public async Task Network_errors_are_retried_then_surfaced_after_the_retry_budget()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));
        var (sink, _) = await CreateAsync(handler, o => o.MaxRetries = 2);

        await Assert.ThrowsAsync<HttpRequestException>(() => sink.SendAsync(Batch(), CancellationToken.None));

        Assert.Equal(3, handler.Calls.Count); // first attempt plus two retries
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Status(HttpStatusCode.Unauthorized)));
        var (sink, delays) = await CreateAsync(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => sink.SendAsync(Batch(), CancellationToken.None));

        Assert.Single(handler.Calls);
        Assert.Empty(delays);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Server_errors_that_never_recover_are_surfaced()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Status(HttpStatusCode.BadGateway)));
        var (sink, _) = await CreateAsync(handler, o => o.MaxRetries = 1);

        await Assert.ThrowsAsync<HttpRequestException>(() => sink.SendAsync(Batch(), CancellationToken.None));

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task A_hung_endpoint_is_cut_off_by_the_export_timeout_and_retried()
    {
        var hanging = new HangingHandler();
        var (sink, _) = await CreateAsync(new FakeHandler((_, _) => Task.FromResult(Status(HttpStatusCode.OK))), o => { o.ExportTimeout = TimeSpan.FromMilliseconds(50); o.MaxRetries = 1; });
        var runtime = typeof(HttpApiPulseSink).GetField("_runtime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(sink)!;
        var hangingSink = new HttpApiPulseSink(() => new HttpClient(hanging), (ApiPulseRuntime)runtime, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hangingSink.SendAsync(Batch(), CancellationToken.None));

        Assert.Equal(2, hanging.Calls); // the timeout is per attempt, so the retry budget still applies
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Is_only_enabled_when_an_endpoint_is_configured()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddApiPulse();
        using var host = builder.Build();
        var withoutEndpoint = new HttpApiPulseSink(() => new HttpClient(), host.Services.GetRequiredService<ApiPulseRuntime>(), (_, _) => Task.CompletedTask);

        var builder2 = Host.CreateApplicationBuilder();
        builder2.Services.AddApiPulse(o => o.Endpoint = new Uri("https://x.example.com"));
        using var host2 = builder2.Build();
        var withEndpoint = new HttpApiPulseSink(() => new HttpClient(), host2.Services.GetRequiredService<ApiPulseRuntime>(), (_, _) => Task.CompletedTask);

        Assert.False(withoutEndpoint.IsEnabled);
        Assert.True(withEndpoint.IsEnabled);
    }

    [Fact]
    public async Task Without_a_sink_or_endpoint_the_application_runs_normally()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { WithSink = false });

        var response = await app.Client.GetAsync("/orders/1");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(app.Sink.Events);
    }
}
