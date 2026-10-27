using System.Text;

namespace ApiPulse.Tests;

public class CaptureTests
{
    [Fact]
    public async Task Minimal_api_request_is_captured_with_metrics_and_route_template()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync("/orders/7?page=2");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var e = await app.SingleEventAsync();
        Assert.Equal("GET", e.Method);
        Assert.Equal("/orders/7", e.Path);
        Assert.Equal("/orders/{id:int}", e.Route);
        Assert.Equal("?page=2", e.Query);
        Assert.Equal(200, e.StatusCode);
        Assert.True(e.DurationMs > 0);
        Assert.InRange(e.Timestamp, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Matches("^[0-9a-f]{32}$", e.TraceId);
        Assert.Matches("^[0-9a-f]{16}$", e.SpanId);
        Assert.True(e.Sampled);
        Assert.NotNull(e.RequestHeaders);
        Assert.Contains("Content-Type", e.ResponseHeaders!.Keys);
    }

    [Fact]
    public async Task Controller_request_is_captured_with_attribute_route()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync("/api/items/5");

        var e = await app.SingleEventAsync();
        Assert.Equal("/api/items/{id:int}", e.Route);
        Assert.Contains("ItemsController", e.Endpoint);
        Assert.Equal(200, e.StatusCode);
        Assert.Contains(e.Logs!, l => l.Message == "Controller loading item 5");
    }

    [Fact]
    public async Task Unmatched_request_has_no_route_but_is_still_reported()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync("/does/not/exist");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var e = await app.SingleEventAsync();
        Assert.Null(e.Route);
        Assert.Equal(404, e.StatusCode);
    }

    [Fact]
    public async Task Event_batch_identifies_service_and_sdk()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.ServiceName = "bookstore"; o.ServiceVersion = "2.3.4"; o.EnvironmentName = "prod"; },
        });

        await app.Client.GetAsync("/orders/1");
        await app.Exporter.FlushAsync();

        var batch = Assert.Single(app.Sink.Batches);
        Assert.Equal("bookstore", batch.Service.Name);
        Assert.Equal("2.3.4", batch.Service.Version);
        Assert.Equal("prod", batch.Service.Environment);
        Assert.Equal("ApiPulse.AspNetCore", batch.Sdk.Name);
        Assert.False(string.IsNullOrEmpty(batch.Sdk.Runtime));
    }

    [Fact]
    public async Task Bodies_are_not_captured_by_default()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.PostAsync("/echo", new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));

        var e = await app.SingleEventAsync();
        Assert.Null(e.RequestBody);
        Assert.Null(e.ResponseBody);
    }

    [Fact]
    public async Task Request_and_response_bodies_are_captured_when_enabled_and_the_application_sees_the_original_body()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.CaptureRequestBody = true; o.CaptureResponseBody = true; },
        });
        const string payload = "{\"hello\":\"world\"}";

        var response = await app.Client.PostAsync("/echo", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(payload, await response.Content.ReadAsStringAsync()); // the application read and wrote the real bytes
        var e = await app.SingleEventAsync();
        Assert.Equal(payload, e.RequestBody);
        Assert.Equal(payload, e.ResponseBody);
        Assert.Equal(payload.Length, e.RequestSize);
        Assert.Equal(payload.Length, e.ResponseSize);
        Assert.False(e.RequestBodyTruncated);
    }

    [Fact]
    public async Task Minimal_api_model_binding_still_works_with_body_capture()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.CaptureRequestBody = true; o.CaptureResponseBody = true; },
        });

        var response = await app.Client.PostAsync(
            "/orders",
            new StringContent("{\"customer\":\"ada\",\"password\":\"p\"}", Encoding.UTF8, "application/json"));

        Assert.Contains("\"customer\":\"ada\"", await response.Content.ReadAsStringAsync());
        var e = await app.SingleEventAsync();
        Assert.Contains("ada", e.RequestBody);
        Assert.Contains("ada", e.ResponseBody);
    }

    [Fact]
    public async Task Large_bodies_are_truncated_but_sizes_stay_exact()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.CaptureResponseBody = true; o.MaxBodySize = 1000; },
        });

        var response = await app.Client.GetAsync("/big");

        Assert.Equal(100_000, (await response.Content.ReadAsStringAsync()).Length);
        var e = await app.SingleEventAsync();
        Assert.Equal(1000, e.ResponseBody!.Length);
        Assert.True(e.ResponseBodyTruncated);
        Assert.Equal(100_000, e.ResponseSize);
    }

    [Fact]
    public async Task Binary_responses_are_never_captured()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.CaptureResponseBody = true,
        });

        await app.Client.GetAsync("/binary");

        var e = await app.SingleEventAsync();
        Assert.Null(e.ResponseBody);
        Assert.Equal(2048, e.ResponseSize);
    }

    [Fact]
    public async Task Uploads_with_ineligible_content_types_are_not_captured()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.CaptureRequestBody = true,
        });

        await app.Client.PostAsync("/echo", new ByteArrayContent(new byte[500]) { Headers = { { "Content-Type", "application/octet-stream" } } });

        var e = await app.SingleEventAsync();
        Assert.Null(e.RequestBody);
    }

    [Fact]
    public async Task Headers_and_query_can_be_switched_off()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.CaptureRequestHeaders = false; o.CaptureResponseHeaders = false; o.CaptureQueryString = false; },
        });

        await app.Client.GetAsync("/search?q=books");

        var e = await app.SingleEventAsync();
        Assert.Null(e.RequestHeaders);
        Assert.Null(e.ResponseHeaders);
        Assert.Null(e.Query);
    }

    [Theory]
    [InlineData(ClientIpMode.None, null)]
    [InlineData(ClientIpMode.Full, "203.0.113.77")]
    [InlineData(ClientIpMode.Anonymized, "203.0.113.0")]
    public async Task Client_ip_follows_the_configured_mode(ClientIpMode mode, string? expected)
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.ClientIp = mode,
            Pipeline = _ => { },
            Services = s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new SetRemoteIpFilter("203.0.113.77")),
        });

        await app.Client.GetAsync("/search");

        var e = await app.SingleEventAsync();
        Assert.Equal(expected, e.ClientIp);
    }

    [Fact]
    public async Task Traceparent_header_is_honoured()
    {
        await using var app = await TestApp.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/search");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        await app.Client.SendAsync(request);

        var e = await app.SingleEventAsync();
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", e.TraceId);
        Assert.Equal("b7ad6b7169203331", e.ParentSpanId);
    }

    [Fact]
    public async Task Metrics_are_published_through_the_ApiPulse_meter_even_without_a_sink()
    {
        var measurements = new List<(string Route, int Status)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ApiPulseDefaults.MeterName && instrument.Name == "apipulse.requests")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string route = "";
            int status = 0;
            foreach (var tag in tags)
            {
                if (tag.Key == "http.route") route = (string)tag.Value!;
                if (tag.Key == "http.response.status_code") status = (int)tag.Value!;
            }

            lock (measurements) { measurements.Add((route, status)); }
        });
        listener.Start();

        await using var app = await TestApp.StartAsync(new TestAppSetup { WithSink = false });

        await app.Client.GetAsync("/orders/3");
        await app.Client.GetAsync("/nope");

        lock (measurements)
        {
            Assert.Contains(("/orders/{id:int}", 200), measurements);
            Assert.Contains(("unmatched", 404), measurements);
        }

        Assert.Empty(app.Sink.Events); // no sink: no event capture at all
    }

    [Fact]
    public async Task Disabled_sdk_is_a_pass_through()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.Enabled = false });

        var response = await app.Client.GetAsync("/orders/1");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await app.EventsAsync());
    }

    private sealed class SetRemoteIpFilter(string address) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
                return nextMiddleware();
            });
            next(app);
        };
    }
}
