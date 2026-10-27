using ApiPulse.Internal;

namespace ApiPulse.Tests;

public class FilteringTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/HEALTH/")]
    public async Task Health_checks_are_excluded_by_default(string path)
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync(path);
        await app.Client.GetAsync("/orders/1");

        var e = await app.SingleEventAsync();
        Assert.Equal("/orders/1", e.Path);
    }

    [Fact]
    public async Task Custom_excluded_paths_support_wildcards()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.ExcludedPaths.Add("/internal/*"),
        });

        await app.Client.GetAsync("/internal/status");
        await app.Client.GetAsync("/health");
        await app.Client.GetAsync("/search");

        Assert.Equal("/search", Assert.Single(await app.EventsAsync()).Path);
    }

    [Fact]
    public async Task Default_exclusions_can_be_cleared()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.ExcludedPaths.Clear(),
        });

        await app.Client.GetAsync("/health");

        Assert.Equal("/health", Assert.Single(await app.EventsAsync()).Path);
    }

    [Fact]
    public async Task Methods_can_be_excluded()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.ExcludedMethods.Add("options"),
        });

        await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/search"));
        await app.Client.GetAsync("/search");

        Assert.Equal("GET", Assert.Single(await app.EventsAsync()).Method);
    }

    [Fact]
    public async Task Filter_callback_can_skip_requests()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.Filter = ctx => !ctx.Request.Headers.ContainsKey("X-Synthetic"),
        });
        using var synthetic = new HttpRequestMessage(HttpMethod.Get, "/search");
        synthetic.Headers.Add("X-Synthetic", "1");

        await app.Client.SendAsync(synthetic);
        await app.Client.GetAsync("/search");

        Assert.Single(await app.EventsAsync());
    }

    [Fact]
    public async Task A_throwing_Filter_never_breaks_the_request()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.Filter = _ => throw new InvalidOperationException("bug in user filter"),
        });

        var response = await app.Client.GetAsync("/search");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Endpoints_marked_with_ExcludeFromApiPulse_are_skipped_for_minimal_apis_groups_and_controllers()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync("/hidden");
        await app.Client.GetAsync("/group/inner");
        await app.Client.GetAsync("/api/items/hidden");
        await app.Client.GetAsync("/search");

        Assert.Equal("/search", Assert.Single(await app.EventsAsync()).Path);
    }
}

public class SamplingTests
{
    [Fact]
    public async Task Rate_one_captures_everything_and_rate_zero_captures_no_successful_requests()
    {
        await using var all = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.SamplingRate = 1 });
        await using var none = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.SamplingRate = 0 });

        for (var i = 0; i < 5; i++)
        {
            await all.Client.GetAsync("/search");
            await none.Client.GetAsync("/search");
        }

        Assert.Equal(5, (await all.EventsAsync()).Count);
        Assert.Empty(await none.EventsAsync());
    }

    [Fact]
    public async Task Fractional_rate_samples_roughly_that_share()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.SamplingRate = 0.5 });

        for (var i = 0; i < 300; i++)
        {
            await app.Client.GetAsync("/search");
        }

        Assert.InRange((await app.EventsAsync()).Count, 100, 200);
    }

    [Theory]
    [InlineData("00000000000000000000000000000001", true)]
    [InlineData("ffffffffffffffffffffffffffffffff", false)]
    public async Task Decision_is_derived_from_the_trace_id_so_services_agree(string traceId, bool sampled)
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.SamplingRate = 0.5 });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/search");
        request.Headers.Add("traceparent", $"00-{traceId}-b7ad6b7169203331-01");

        await app.Client.SendAsync(request);

        Assert.Equal(sampled ? 1 : 0, (await app.EventsAsync()).Count);
    }

    [Fact]
    public async Task Errors_are_always_reported_even_when_unsampled_but_without_heavy_data()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            UseExceptionHandler = true,
            Configure = o => { o.SamplingRate = 0; o.CaptureRequestBody = true; },
        });

        await app.Client.GetAsync("/boom");
        await app.Client.GetAsync("/search");

        var e = await app.SingleEventAsync();
        Assert.Equal("/boom", e.Path);
        Assert.Equal(500, e.StatusCode);
        Assert.False(e.Sampled);
        Assert.Null(e.RequestHeaders);
        Assert.Null(e.Spans);
        Assert.NotNull(e.Exceptions);
        Assert.DoesNotContain(e.Logs ?? new(), l => l.Level == "Information"); // only warnings and above are kept
    }

    [Fact]
    public async Task Always_capture_errors_can_be_turned_off()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            UseExceptionHandler = true,
            Configure = o => { o.SamplingRate = 0; o.AlwaysCaptureErrors = false; },
        });

        await app.Client.GetAsync("/boom");

        Assert.Empty(await app.EventsAsync());
    }

    [Fact]
    public async Task Slow_requests_can_be_promoted()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.SamplingRate = 0; o.SlowRequestThreshold = TimeSpan.FromMilliseconds(1); },
        });

        await app.Client.GetAsync("/orders/1"); // sleeps at least a millisecond

        var e = await app.SingleEventAsync();
        Assert.False(e.Sampled);
    }

    [Fact]
    public async Task Sampler_callback_overrides_the_rate_and_can_defer_to_it()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o =>
            {
                o.SamplingRate = 0;
                o.Sampler = ctx => ctx.Request.Path.StartsWithSegments("/orders") ? true : null;
            },
        });

        await app.Client.GetAsync("/orders/1");
        await app.Client.GetAsync("/search"); // sampler abstains, rate 0 applies

        Assert.Equal("/orders/1", Assert.Single(await app.EventsAsync()).Path);
    }

    [Fact]
    public async Task Metrics_cover_every_request_regardless_of_sampling()
    {
        var count = 0;
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
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, (int)value));
        listener.Start();
        await using var app = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.SamplingRate = 0 });

        for (var i = 0; i < 5; i++)
        {
            await app.Client.GetAsync("/search");
        }

        Assert.Empty(await app.EventsAsync());
        Assert.True(Volatile.Read(ref count) >= 5);
    }

    [Fact]
    public async Task Keep_forces_an_unsampled_request_to_be_reported_and_Suppress_drops_a_sampled_one()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.SamplingRate = 0 });
        await using var all = await TestApp.StartAsync();

        await app.Client.GetAsync("/keep");
        await all.Client.GetAsync("/suppress");
        await all.Client.GetAsync("/search");

        Assert.Equal("/keep", Assert.Single(await app.EventsAsync()).Path);
        Assert.Equal("/search", Assert.Single(await all.EventsAsync()).Path);
    }
}

public class MatchingTests
{
    [Theory]
    [InlineData("/health", "/health", true)]
    [InlineData("/health", "/HEALTH", true)]
    [InlineData("/health", "/health/", true)]
    [InlineData("/health", "/healthy", false)]
    [InlineData("/health/*", "/health/live", true)]
    [InlineData("/health/*", "/health", false)]
    [InlineData("/api/*/internal", "/api/v1/internal", true)]
    [InlineData("/api/*/internal", "/api/v1/public", false)]
    [InlineData("/a?c", "/abc", true)]
    [InlineData("*", "/anything", true)]
    public void Path_matching(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, new PathMatcher(new[] { pattern }).IsMatch(path));
    }

    [Theory]
    [InlineData("*token*", "X-Refresh-Token-Id", true)]
    [InlineData("auth*", "Authorization", true)]
    [InlineData("auth*", "x-auth", false)]
    [InlineData("exact", "EXACT", true)]
    public void Name_matching_is_case_insensitive_with_wildcards(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, new NameMatcher(new[] { pattern }).IsMatch(name));
    }

    [Theory]
    [InlineData("203.0.113.77", ClientIpMode.Anonymized, "203.0.113.0")]
    [InlineData("203.0.113.77", ClientIpMode.Full, "203.0.113.77")]
    [InlineData("203.0.113.77", ClientIpMode.None, null)]
    [InlineData("2001:db8:abcd:1234:5678:9abc:def0:1234", ClientIpMode.Anonymized, "2001:db8:abcd::")]
    [InlineData("::ffff:198.51.100.9", ClientIpMode.Anonymized, "198.51.100.0")]
    public void Client_ip_formatting(string address, ClientIpMode mode, string? expected)
    {
        Assert.Equal(expected, ClientIpFormatter.Format(System.Net.IPAddress.Parse(address), mode));
    }
}
