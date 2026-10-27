using Microsoft.AspNetCore.Builder;

namespace ApiPulse.Tests;

public class ErrorTests
{
    [Fact]
    public async Task Unhandled_exceptions_are_captured_and_still_propagate_to_the_host()
    {
        await using var app = await TestApp.StartAsync();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => app.Client.GetAsync("/boom"));

        Assert.Equal("kaboom", thrown.Message); // the SDK did not swallow or wrap it
        var e = await app.SingleEventAsync();
        Assert.Equal(500, e.StatusCode);
        var error = Assert.Single(e.Exceptions!);
        Assert.Equal("System.InvalidOperationException", error.Type);
        Assert.Equal("kaboom", error.Message);
        Assert.False(error.Handled);
        Assert.Contains("MapEndpoints", error.StackTrace);
        Assert.Equal(16, error.Fingerprint.Length);
        Assert.Equal("/boom", e.Route);
    }

    [Fact]
    public async Task Exceptions_turned_into_responses_by_the_error_handler_are_captured_with_their_route()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { UseExceptionHandler = true });

        var response = await app.Client.GetAsync("/boom");

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        var e = await app.SingleEventAsync();
        Assert.Equal(500, e.StatusCode);
        Assert.Equal("/boom", e.Route); // the handler clears the endpoint; the SDK recovers it
        var error = Assert.Single(e.Exceptions!, x => x.Type == "System.InvalidOperationException" && x.Message == "kaboom");
        Assert.False(error.Handled); // it escaped the endpoint, even though the error handler produced the response
        Assert.Contains(e.Logs!, l => l.Message == "about to fail"); // logs before the failure are kept
    }

    [Fact]
    public async Task Developer_exception_page_errors_are_reported_as_unhandled()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { UseDeveloperExceptionPage = true });

        var response = await app.Client.GetAsync("/boom");

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        var e = await app.SingleEventAsync();
        var error = Assert.Single(e.Exceptions!, x => x.Message == "kaboom");
        Assert.False(error.Handled);
        Assert.Equal("/boom", e.Route);
    }

    [Fact]
    public async Task Controller_exceptions_are_captured()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { UseExceptionHandler = true });

        await app.Client.GetAsync("/api/items/fail");

        var e = await app.SingleEventAsync();
        Assert.Equal(500, e.StatusCode);
        Assert.Contains(e.Exceptions!, x => x.Message == "controller failure" && !x.Handled);
    }

    [Fact]
    public async Task Identical_bugs_share_a_fingerprint_and_different_bugs_do_not()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            UseExceptionHandler = true,
            Pipeline = a => a.MapGet("/other", () => { throw new ArgumentException("different"); }),
        });

        await app.Client.GetAsync("/boom");
        await app.Client.GetAsync("/boom");
        await app.Client.GetAsync("/other");

        var events = await app.EventsAsync();
        var prints = events.Select(e => e.Exceptions!.First().Fingerprint).ToArray();
        Assert.Equal(prints[0], prints[1]);
        Assert.NotEqual(prints[0], prints[2]);
    }

    [Fact]
    public async Task Inner_exceptions_are_captured()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            UseExceptionHandler = true,
            Pipeline = a => a.MapGet("/wrapped", () => { throw new InvalidOperationException("outer", new TimeoutException("inner")); }),
        });

        await app.Client.GetAsync("/wrapped");

        var error = (await app.SingleEventAsync()).Exceptions!.First(x => x.Message == "outer");
        Assert.Equal("inner", error.Inner!.Message);
    }

    [Fact]
    public async Task Client_disconnects_are_reported_as_aborted_not_as_errors()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Pipeline = a => a.MapGet("/slow", async (HttpContext ctx) =>
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                return Results.Ok();
            }),
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Client.GetAsync("/slow", cts.Token));

        var e = await app.SingleEventAsync();
        Assert.True(e.Aborted);
        Assert.Equal(499, e.StatusCode);
        Assert.Null(e.Exceptions);
    }
}

public class TraceTests
{
    [Fact]
    public async Task Custom_spans_are_attached_with_safe_tags_only()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync("/spans");

        var e = await app.SingleEventAsync();
        var span = Assert.Single(e.Spans!);
        Assert.Equal("db.query", span.Name);
        Assert.Equal("Client", span.Kind);
        Assert.Equal("ApiPulse", span.Source);
        Assert.True(span.DurationMs >= 4);
        Assert.True(span.StartOffsetMs >= 0);
        Assert.Equal("postgresql", span.Tags!["db.system"]);
        Assert.DoesNotContain("db.statement", span.Tags.Keys); // free-form statements may hold user data
        Assert.Equal("https://api.example.com/v1/x?token=******&page=2", span.Tags["url.full"]);
        Assert.Matches("^[0-9a-f]{16}$", span.SpanId);
    }

    [Fact]
    public async Task Spans_from_additional_sources_are_collected_when_configured()
    {
        using var source = new System.Diagnostics.ActivitySource("MyCompany.Billing");
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.SpanSources.Add("MyCompany.*"),
            Pipeline = a => a.MapGet("/billing", async () =>
            {
                using (source.StartActivity("charge-card"))
                {
                    using (source.StartActivity("nested"))
                    {
                        await Task.Delay(2);
                    }
                }

                return Results.Ok();
            }),
        });

        await app.Client.GetAsync("/billing");

        var spans = (await app.SingleEventAsync()).Spans!;
        Assert.Equal(new[] { "charge-card", "nested" }, spans.Select(s => s.Name).OrderBy(n => n).ToArray());
        var nested = spans.Single(s => s.Name == "nested");
        Assert.Equal(spans.Single(s => s.Name == "charge-card").SpanId, nested.ParentSpanId);
    }

    [Fact]
    public async Task Spans_are_bounded_per_request()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.MaxSpansPerRequest = 2,
            Pipeline = a => a.MapGet("/many", () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    using var s = ApiPulseActivitySource.Instance.StartActivity($"op{i}");
                }

                return Results.Ok();
            }),
        });

        await app.Client.GetAsync("/many");

        var e = await app.SingleEventAsync();
        Assert.Equal(2, e.Spans!.Count);
        Assert.Equal(3, e.DroppedSpans);
    }

    [Fact]
    public async Task Span_capture_can_be_disabled()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.CaptureSpans = false });

        await app.Client.GetAsync("/spans");

        Assert.Null((await app.SingleEventAsync()).Spans);
    }

    [Fact]
    public async Task Concurrent_requests_do_not_mix_up_their_spans()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Pipeline = a => a.MapGet("/work/{n:int}", async (int n) =>
            {
                using var s = ApiPulseActivitySource.Instance.StartActivity($"work-{n}");
                await Task.Delay(Random.Shared.Next(1, 20));
                return Results.Ok();
            }),
        });

        await Task.WhenAll(Enumerable.Range(1, 40).Select(i => app.Client.GetAsync($"/work/{i}")));

        foreach (var e in await app.EventsAsync())
        {
            var n = e.Path.Split('/')[^1];
            Assert.Equal($"work-{n}", Assert.Single(e.Spans!).Name);
        }
    }
}
