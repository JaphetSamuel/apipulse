using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPulse.Tests;

public class ContextTests
{
    [Fact]
    public async Task Consumer_and_tags_set_through_injected_context_and_HttpContext_are_attached()
    {
        await using var app = await TestApp.StartAsync();

        var body = await app.Client.GetStringAsync("/ctx");

        var e = await app.SingleEventAsync();
        Assert.Equal(new ApiConsumer("acme-42", "ACME Corp", "enterprise"), e.Consumer);
        Assert.Equal("acme", e.Tags!["tenant"]);
        Assert.Equal("beta", e.Tags["feature"]);
        Assert.Contains("\"active\":true", body);
        Assert.Contains(e.TraceId!, body);
    }

    [Fact]
    public async Task Consumer_resolver_runs_when_the_request_finishes()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.ConsumerResolver = ctx =>
                ctx.Request.Headers.TryGetValue("X-Client-Id", out var id) ? new ApiConsumer(id.ToString(), Group: "partners") : null,
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/search");
        request.Headers.Add("X-Client-Id", "client-7");

        await app.Client.SendAsync(request);

        Assert.Equal(new ApiConsumer("client-7", null, "partners"), (await app.SingleEventAsync()).Consumer);
    }

    [Fact]
    public async Task Authenticated_user_becomes_the_consumer_automatically()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Services = s => s.AddSingleton<IStartupFilter>(new AuthenticateFilter()),
        });

        await app.Client.GetAsync("/search");

        Assert.Equal(new ApiConsumer("user-1", "Alice"), (await app.SingleEventAsync()).Consumer);
    }

    [Fact]
    public async Task Automatic_detection_can_be_disabled()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.AutoDetectConsumer = false,
            Services = s => s.AddSingleton<IStartupFilter>(new AuthenticateFilter()),
        });

        await app.Client.GetAsync("/search");

        Assert.Null((await app.SingleEventAsync()).Consumer);
    }

    [Fact]
    public async Task Explicit_consumer_wins_over_the_resolver_and_the_authenticated_user()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.ConsumerResolver = _ => new ApiConsumer("from-resolver"),
            Services = s => s.AddSingleton<IStartupFilter>(new AuthenticateFilter()),
        });

        await app.Client.GetAsync("/ctx");

        Assert.Equal("acme-42", (await app.SingleEventAsync()).Consumer!.Id);
    }

    [Fact]
    public async Task Tags_are_bounded_masked_and_removable()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Pipeline = a => a.MapGet("/tags", (IApiPulseContext pulse) =>
            {
                for (var i = 0; i < 40; i++)
                {
                    pulse.SetTag($"k{i}", "v");
                }

                pulse.SetTag("api_token", "sensitive");
                pulse.SetTag("k0", null); // removed, which frees a slot
                pulse.SetTag("long", new string('x', 1000));
                return Results.Ok();
            }),
        });

        await app.Client.GetAsync("/tags");

        var tags = (await app.SingleEventAsync()).Tags!;
        Assert.True(tags.Count <= 20);
        Assert.DoesNotContain("k0", tags.Keys);
        Assert.DoesNotContain("sensitive", tags.Values);
        Assert.True(tags.GetValueOrDefault("long", "")!.Length <= 256);
    }

    [Fact]
    public async Task Context_is_an_inert_no_op_outside_a_monitored_request()
    {
        await using var app = await TestApp.StartAsync();
        var pulse = app.App.Services.GetRequiredService<IApiPulseContext>();

        pulse.SetConsumer("x");
        pulse.SetTag("a", "b");
        pulse.RecordException(new Exception("ignored"));

        Assert.False(pulse.IsActive);
        Assert.Null(pulse.TraceId);
        Assert.Empty(await app.EventsAsync());
    }

    private sealed class AuthenticateFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) =>
            {
                ctx.User = TestApp.User("user-1", "Alice");
                return n();
            });
            next(app);
        };
    }
}

public class LogCorrelationTests
{
    [Fact]
    public async Task Logs_written_during_a_request_are_attached_to_it_with_structure()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync("/orders/9");

        var e = await app.SingleEventAsync();
        Assert.Equal(2, e.Logs!.Count);
        var first = e.Logs[0];
        Assert.Equal("Loading order 9", first.Message);
        Assert.Equal("Information", first.Level);
        Assert.Equal("ApiPulse.Tests.TestApp", first.Category);
        Assert.Equal("9", first.Properties!["OrderId"]);
        Assert.DoesNotContain("{OriginalFormat}", first.Properties.Keys);
        Assert.InRange(first.Timestamp, e.Timestamp, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Concurrent_requests_never_see_each_others_logs()
    {
        await using var app = await TestApp.StartAsync();

        await Task.WhenAll(Enumerable.Range(1, 60).Select(i => app.Client.GetAsync($"/orders/{i}")));

        var events = await app.EventsAsync();
        Assert.Equal(60, events.Count);
        Assert.Equal(60, events.Select(e => e.TraceId).Distinct().Count());
        foreach (var e in events)
        {
            var id = e.Path.Split('/')[^1];
            Assert.Equal(2, e.Logs!.Count);
            Assert.All(e.Logs, l => Assert.EndsWith($"order {id}", l.Message));
        }
    }

    [Fact]
    public async Task Log_level_and_per_request_limits_are_enforced()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            MinimumLevel = LogLevel.Trace,
            Configure = o => { o.MinimumLogLevel = LogLevel.Information; o.MaxLogsPerRequest = 3; },
        });

        await app.Client.GetAsync("/logs"); // debug entry is below the minimum
        await app.Client.GetAsync("/log-flood");

        var events = await app.EventsAsync();
        var logs = events.Single(e => e.Path == "/logs").Logs!;
        Assert.DoesNotContain(logs, l => l.Message == "debug detail");

        var flood = events.Single(e => e.Path == "/log-flood");
        Assert.Equal(3, flood.Logs!.Count);
        Assert.Equal(7, flood.DroppedLogs);
    }

    [Fact]
    public async Task Log_text_and_sensitive_properties_are_masked_and_own_category_is_ignored()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync("/logs");

        var e = await app.SingleEventAsync();
        Assert.DoesNotContain(e.Logs!, l => l.Category == "ApiPulse.Exporter");
        var info = e.Logs!.Single(l => l.Level == "Information");
        Assert.Equal("******", info.Properties!["Password"]);
        Assert.Equal("bob", info.Properties["UserName"]);
        Assert.DoesNotContain("abc.def.ghi", e.Logs!.Single(l => l.Level == "Warning").Message);
    }

    [Fact]
    public async Task Logs_from_tasks_started_by_the_request_are_correlated()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync("/background-log");

        var e = await app.SingleEventAsync();
        Assert.Contains(e.Logs!, l => l.Message == "from a task started by the request");
    }

    [Fact]
    public async Task Logs_outside_any_request_are_ignored()
    {
        await using var app = await TestApp.StartAsync();

        app.App.Services.GetRequiredService<ILogger<LogCorrelationTests>>().LogError("not in a request");

        Assert.Empty(await app.EventsAsync());
    }

    [Fact]
    public async Task Log_capture_can_be_disabled()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Configure = o => o.CaptureLogs = false });

        await app.Client.GetAsync("/orders/1");

        Assert.Null((await app.SingleEventAsync()).Logs);
    }

    [Fact]
    public async Task Logged_exceptions_are_reported_once_even_when_also_recorded_explicitly()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync("/log-exception");

        var e = await app.SingleEventAsync();
        var error = Assert.Single(e.Exceptions!);
        Assert.Equal("System.TimeoutException", error.Type);
        Assert.True(error.Handled);
        Assert.Equal("System.TimeoutException", e.Logs!.Single(l => l.Level == "Error").Exception!.Type);
    }

    [Fact]
    public async Task The_application_still_logs_normally_to_its_other_providers()
    {
        await using var app = await TestApp.StartAsync();

        await app.Client.GetAsync("/orders/4");

        Assert.Contains(app.AppLogs.Entries, x => x.Message == "Loading order 4");
    }
}
