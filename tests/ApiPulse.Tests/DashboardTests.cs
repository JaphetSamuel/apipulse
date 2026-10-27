using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ApiPulse.Internal;
using Microsoft.Extensions.Hosting;

namespace ApiPulse.Tests;

public class DashboardTests
{
    private static TestAppSetup Dev(Action<ApiPulseOptions>? configure = null) => new()
    {
        Environment = "Development",
        WithSink = false, // prove the dashboard works with no endpoint and no sink at all
        Configure = configure,
    };

    private static Task<JsonElement> Get(TestApp app, string url) => app.Client.GetFromJsonAsync<JsonElement>(url);

    private static JsonElement[] Items(JsonElement root) => root.GetProperty("items").EnumerateArray().ToArray();

    // ------------------------------------------------------------------ access

    [Fact]
    public async Task Dashboard_page_is_served_in_development_with_strict_security_headers()
    {
        await using var app = await TestApp.StartAsync(Dev());

        var response = await app.Client.GetAsync("/apipulse/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("ApiPulse", await response.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Path_without_trailing_slash_redirects_so_relative_urls_work()
    {
        await using var app = await TestApp.StartAsync(Dev());

        var response = await app.Client.GetAsync("/apipulse");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/apipulse/", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task Dashboard_is_forbidden_outside_development_by_default(string environment)
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Environment = environment });

        var page = await app.Client.GetAsync("/apipulse/");
        var api = await app.Client.GetAsync("/apipulse/api/requests");

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
        var message = await page.Content.ReadAsStringAsync();
        Assert.Contains("Development", message);
        Assert.Contains($"'{environment}'", message); // tells you what the app is actually running as
        Assert.Contains("ASPNETCORE_ENVIRONMENT", message);
        Assert.Contains("Dashboard.Authorize", message);
    }

    [Fact]
    public async Task Authorize_callback_opens_the_dashboard_in_any_environment_for_allowed_callers_only()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Environment = "Production",
            WithSink = false,
            Configure = o => o.Dashboard.Authorize = ctx => ctx.Request.Headers.ContainsKey("X-Admin"),
        });
        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/apipulse/api/overview");
        allowed.Headers.Add("X-Admin", "1");

        Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(allowed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/apipulse/api/overview")).StatusCode);
    }

    [Fact]
    public async Task A_throwing_Authorize_callback_denies_access()
    {
        await using var app = await TestApp.StartAsync(Dev(o => o.Dashboard.Authorize = _ => throw new InvalidOperationException("bug")));

        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/apipulse/")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_can_be_disabled_and_the_request_falls_through_to_the_application()
    {
        await using var app = await TestApp.StartAsync(Dev(o => o.Dashboard.Enabled = false));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/apipulse/")).StatusCode);
        await app.Client.GetAsync("/search");
        Assert.Empty(await app.EventsAsync()); // nothing is stored or captured for it either
    }

    [Fact]
    public async Task Dashboard_can_be_switched_off_with_an_environment_variable()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Environment = "Development",
            Env = new() { ["APIPULSE_DASHBOARD"] = "false" },
        });

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/apipulse/")).StatusCode);
    }

    [Fact]
    public async Task Custom_path_is_honoured_and_the_default_path_is_left_to_the_application()
    {
        await using var app = await TestApp.StartAsync(Dev(o => o.Dashboard.Path = "telescope/"));

        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/telescope/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/apipulse/")).StatusCode);
    }

    [Fact]
    public async Task Unknown_dashboard_paths_return_404()
    {
        await using var app = await TestApp.StartAsync(Dev());

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/apipulse/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/apipulse/api/nope")).StatusCode);
    }

    // ------------------------------------------------------------------ capture without any destination

    [Fact]
    public async Task Requests_appear_in_the_dashboard_immediately_with_no_endpoint_or_sink_configured()
    {
        await using var app = await TestApp.StartAsync(Dev());

        await app.Client.GetAsync("/orders/7?page=2");

        var list = Items(await Get(app, "/apipulse/api/requests"));
        var item = Assert.Single(list); // no flush needed: the dashboard is fed synchronously
        Assert.Equal("GET", item.GetProperty("method").GetString());
        Assert.Equal("/orders/{id:int}", item.GetProperty("route").GetString());
        Assert.Equal(200, item.GetProperty("status").GetInt32());
        Assert.Equal(2, item.GetProperty("logs").GetInt32());
    }

    [Fact]
    public async Task The_dashboards_own_traffic_never_appears_in_it()
    {
        await using var app = await TestApp.StartAsync(Dev());

        await app.Client.GetAsync("/apipulse/");
        await app.Client.GetAsync("/apipulse/api/overview");
        await app.Client.GetAsync("/search");

        var paths = Items(await Get(app, "/apipulse/api/requests")).Select(i => i.GetProperty("path").GetString());
        Assert.Equal(new[] { "/search" }, paths);
    }

    [Fact]
    public async Task Dashboard_and_exporter_both_receive_events()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Environment = "Development" });

        await app.Client.GetAsync("/search");

        Assert.Single(Items(await Get(app, "/apipulse/api/requests")));
        Assert.Single(await app.EventsAsync());
    }

    [Fact]
    public async Task Masking_applies_to_what_the_dashboard_shows()
    {
        await using var app = await TestApp.StartAsync(Dev(o => { o.CaptureRequestBody = true; o.CaptureResponseBody = true; }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders?token=q-secret")
        {
            Content = new StringContent("{\"customer\":\"ada\",\"password\":\"hunter2\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer h-secret");
        await app.Client.SendAsync(request);

        var id = Items(await Get(app, "/apipulse/api/requests"))[0].GetProperty("id").GetString();
        var raw = await app.Client.GetStringAsync($"/apipulse/api/requests/{id}");

        Assert.Contains("ada", raw);
        foreach (var secret in new[] { "hunter2", "h-secret", "q-secret", "response-secret" })
        {
            Assert.DoesNotContain(secret, raw);
        }
    }

    // ------------------------------------------------------------------ bodies in development

    private static async Task<JsonElement> PostOrderAndGetDetail(TestApp app)
    {
        await app.Client.PostAsync(
            "/orders?token=q-secret",
            new StringContent("{\"customer\":\"ada\",\"password\":\"hunter2\"}", Encoding.UTF8, "application/json"));
        var id = Items(await Get(app, "/apipulse/api/requests"))[0].GetProperty("id").GetString();
        return await Get(app, $"/apipulse/api/requests/{id}");
    }

    [Fact]
    public async Task Bodies_are_captured_by_default_in_development_and_stay_masked()
    {
        await using var app = await TestApp.StartAsync(Dev()); // no capture options set at all

        var detail = await PostOrderAndGetDetail(app);

        var request = detail.GetProperty("requestBody").GetString()!;
        var response = detail.GetProperty("responseBody").GetString()!;
        Assert.Contains("ada", request);
        Assert.Contains("ada", response);
        Assert.DoesNotContain("hunter2", request);
        Assert.DoesNotContain("response-secret", response);
        Assert.DoesNotContain("q-secret", detail.GetRawText());
        Assert.True((await Get(app, "/apipulse/api/info")).GetProperty("bodiesCaptured").GetBoolean());
    }

    [Fact]
    public async Task An_explicit_false_beats_the_development_default()
    {
        await using var app = await TestApp.StartAsync(Dev(o => { o.CaptureRequestBody = false; o.CaptureResponseBody = false; }));

        var detail = await PostOrderAndGetDetail(app);

        Assert.False(detail.TryGetProperty("requestBody", out _));
        Assert.False(detail.TryGetProperty("responseBody", out _));
    }

    [Fact]
    public async Task Each_direction_can_be_overridden_independently()
    {
        await using var app = await TestApp.StartAsync(Dev(o => o.CaptureResponseBody = false));

        var detail = await PostOrderAndGetDetail(app);

        Assert.True(detail.TryGetProperty("requestBody", out _));
        Assert.False(detail.TryGetProperty("responseBody", out _));
    }

    [Fact]
    public async Task Bodies_stay_off_by_default_outside_development_even_when_the_dashboard_is_enabled()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Environment = "Production",
            WithSink = false,
            Configure = o => o.Dashboard.Authorize = _ => true,
        });

        var detail = await PostOrderAndGetDetail(app);

        Assert.False(detail.TryGetProperty("requestBody", out _));
        Assert.False(detail.TryGetProperty("responseBody", out _));
        Assert.False((await Get(app, "/apipulse/api/info")).GetProperty("bodiesCaptured").GetBoolean());
    }

    [Fact]
    public async Task Bodies_are_not_captured_in_development_when_the_dashboard_is_off()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Environment = "Development",
            Configure = o => o.Dashboard.Enabled = false, // only the collecting sink is active
        });
        await app.Client.PostAsync("/orders", new StringContent("{\"customer\":\"ada\",\"password\":\"x\"}", Encoding.UTF8, "application/json"));

        var e = await app.SingleEventAsync();

        Assert.Null(e.RequestBody);
        Assert.Null(e.ResponseBody);
    }

    [Fact]
    public async Task The_environment_variable_can_force_bodies_off_in_development()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Environment = "Development",
            WithSink = false,
            Env = new() { ["APIPULSE_CAPTURE_REQUEST_BODY"] = "false", ["APIPULSE_CAPTURE_RESPONSE_BODY"] = "0" },
        });

        var detail = await PostOrderAndGetDetail(app);

        Assert.False(detail.TryGetProperty("requestBody", out _));
        Assert.False(detail.TryGetProperty("responseBody", out _));
    }

    // ------------------------------------------------------------------ requests api

    [Fact]
    public async Task Requests_are_listed_newest_first_and_can_be_filtered()
    {
        await using var app = await TestApp.StartAsync(Dev(o => o.ConsumerResolver = ctx => new ApiConsumer(ctx.Request.Path.StartsWithSegments("/orders") ? "acme" : "other")));
        await app.Client.GetAsync("/orders/1");
        await app.Client.GetAsync("/search?q=books");
        await app.Client.PostAsync("/echo", new StringContent("x", Encoding.UTF8, "text/plain"));
        await app.Client.GetAsync("/nope");

        var all = Items(await Get(app, "/apipulse/api/requests"));
        Assert.Equal(new[] { "/nope", "/echo", "/search", "/orders/1" }, all.Select(i => i.GetProperty("path").GetString()).ToArray());

        Assert.Single(Items(await Get(app, "/apipulse/api/requests?status=4xx")));
        Assert.Single(Items(await Get(app, "/apipulse/api/requests?status=404")));
        Assert.Equal(3, Items(await Get(app, "/apipulse/api/requests?status=2xx")).Length);
        Assert.Single(Items(await Get(app, "/apipulse/api/requests?method=post")));
        Assert.Single(Items(await Get(app, "/apipulse/api/requests?q=BOOKS")));
        Assert.Single(Items(await Get(app, "/apipulse/api/requests?consumer=acme")));
        Assert.Single(Items(await Get(app, "/apipulse/api/requests?route=/orders/%7Bid:int%7D")));
    }

    [Fact]
    public async Task Errors_filter_returns_failed_requests_only()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Environment = "Development", UseExceptionHandler = true, WithSink = false });
        await app.Client.GetAsync("/search");
        await app.Client.GetAsync("/boom");

        var failed = Items(await Get(app, "/apipulse/api/requests?errors=1"));

        var only = Assert.Single(failed);
        Assert.Equal("/boom", only.GetProperty("path").GetString());
        Assert.Equal(1, only.GetProperty("exceptions").GetInt32());
    }

    [Fact]
    public async Task Paging_uses_cursors_and_reports_whether_more_exist()
    {
        await using var app = await TestApp.StartAsync(Dev());
        for (var i = 0; i < 7; i++)
        {
            await app.Client.GetAsync($"/orders/{i}");
        }

        var first = await Get(app, "/apipulse/api/requests?limit=3");
        Assert.Equal(3, Items(first).Length);
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(7, first.GetProperty("total").GetInt32());
        var oldestSeq = Items(first)[^1].GetProperty("seq").GetInt64();

        var second = await Get(app, $"/apipulse/api/requests?limit=10&before={oldestSeq}");
        Assert.Equal(4, Items(second).Length);
        Assert.False(second.GetProperty("hasMore").GetBoolean());

        var latest = first.GetProperty("latest").GetInt64();
        await app.Client.GetAsync("/search");
        var fresh = Items(await Get(app, $"/apipulse/api/requests?after={latest}"));
        Assert.Equal("/search", Assert.Single(fresh).GetProperty("path").GetString());
    }

    [Fact]
    public async Task Request_details_include_logs_spans_and_exceptions_and_unknown_ids_return_404()
    {
        await using var app = await TestApp.StartAsync(Dev());
        await app.Client.GetAsync("/spans");
        var id = Items(await Get(app, "/apipulse/api/requests"))[0].GetProperty("id").GetString();

        var detail = await Get(app, $"/apipulse/api/requests/{id}");

        Assert.Equal("db.query", detail.GetProperty("spans")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/apipulse/api/requests/does-not-exist")).StatusCode);
    }

    // ------------------------------------------------------------------ overview, exceptions, logs, spans

    [Fact]
    public async Task Overview_summarises_traffic_status_latency_and_busiest_routes()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Environment = "Development", UseExceptionHandler = true, WithSink = false });
        for (var i = 0; i < 4; i++)
        {
            await app.Client.GetAsync($"/orders/{i}");
        }

        await app.Client.GetAsync("/nope");
        await app.Client.GetAsync("/boom");

        var o = await Get(app, "/apipulse/api/overview?window=60");

        Assert.Equal(6, o.GetProperty("total").GetInt32());
        var status = o.GetProperty("status");
        Assert.Equal(4, status.GetProperty("c2xx").GetInt32());
        Assert.Equal(1, status.GetProperty("c4xx").GetInt32());
        Assert.Equal(1, status.GetProperty("c5xx").GetInt32());
        Assert.Equal(1, o.GetProperty("requestsWithExceptions").GetInt32());
        Assert.True(o.GetProperty("p95Ms").GetDouble() >= o.GetProperty("p50Ms").GetDouble());
        Assert.Equal(30, o.GetProperty("buckets").GetArrayLength());
        Assert.Equal(6, o.GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        var top = o.GetProperty("topRoutes")[0];
        Assert.Equal("/orders/{id:int}", top.GetProperty("route").GetString());
        Assert.Equal(4, top.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Overview_of_an_empty_store_is_well_formed()
    {
        await using var app = await TestApp.StartAsync(Dev());

        var o = await Get(app, "/apipulse/api/overview");

        Assert.Equal(0, o.GetProperty("total").GetInt32());
        Assert.Equal(0, o.GetProperty("p95Ms").GetDouble());
        Assert.Equal(0, o.GetProperty("topRoutes").GetArrayLength());
    }

    [Fact]
    public async Task Identical_exceptions_are_grouped_into_one_row_with_a_count()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { Environment = "Development", UseExceptionHandler = true, WithSink = false });
        await app.Client.GetAsync("/boom");
        await app.Client.GetAsync("/boom");
        await app.Client.GetAsync("/boom");

        var group = Assert.Single(Items(await Get(app, "/apipulse/api/exceptions")));

        Assert.Equal(3, group.GetProperty("count").GetInt32());
        Assert.Equal("System.InvalidOperationException", group.GetProperty("type").GetString());
        Assert.True(group.GetProperty("unhandled").GetBoolean());
        Assert.Equal("GET /boom", group.GetProperty("routes")[0].GetString());
        Assert.False(string.IsNullOrEmpty(group.GetProperty("latestRequestId").GetString()));
    }

    [Fact]
    public async Task Logs_are_listed_across_requests_with_level_and_text_filters()
    {
        await using var app = await TestApp.StartAsync(Dev());
        await app.Client.GetAsync("/orders/3");
        await app.Client.GetAsync("/logs");

        var all = Items(await Get(app, "/apipulse/api/logs"));
        Assert.Contains(all, l => l.GetProperty("message").GetString() == "Loading order 3");
        Assert.All(all, l => Assert.False(string.IsNullOrEmpty(l.GetProperty("requestId").GetString())));

        var warnings = Items(await Get(app, "/apipulse/api/logs?level=Warning"));
        Assert.All(warnings, l => Assert.Equal("Warning", l.GetProperty("level").GetString()));
        Assert.NotEmpty(warnings);

        Assert.Single(Items(await Get(app, "/apipulse/api/logs?q=Loaded%20order")));
    }

    [Fact]
    public async Task Spans_are_listed_with_the_request_they_belong_to()
    {
        await using var app = await TestApp.StartAsync(Dev());
        await app.Client.GetAsync("/spans");

        var span = Assert.Single(Items(await Get(app, "/apipulse/api/spans")));

        Assert.Equal("db.query", span.GetProperty("name").GetString());
        Assert.Equal("GET /spans", span.GetProperty("request").GetString());
        Assert.Single(Items(await Get(app, "/apipulse/api/spans?q=postgres")).Concat(Items(await Get(app, "/apipulse/api/spans?q=db.query"))).Take(1));
    }

    // ------------------------------------------------------------------ clear and capacity

    [Fact]
    public async Task Clear_requires_a_post_with_the_anti_csrf_header()
    {
        await using var app = await TestApp.StartAsync(Dev());
        await app.Client.GetAsync("/search");

        var noHeader = await app.Client.PostAsync("/apipulse/api/clear", null);
        var wrongMethod = await app.Client.GetAsync("/apipulse/api/clear");
        Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongMethod.StatusCode);
        Assert.Single(Items(await Get(app, "/apipulse/api/requests")));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/apipulse/api/clear");
        request.Headers.Add("X-ApiPulse", "1");
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.SendAsync(request)).StatusCode);
        Assert.Empty(Items(await Get(app, "/apipulse/api/requests")));
    }

    [Fact]
    public async Task Only_the_most_recent_entries_are_kept()
    {
        await using var app = await TestApp.StartAsync(Dev(o => o.Dashboard.MaxEntries = 10));
        for (var i = 0; i < 15; i++)
        {
            await app.Client.GetAsync($"/orders/{i}");
        }

        var list = await Get(app, "/apipulse/api/requests?limit=200");

        var items = Items(list);
        Assert.Equal(10, items.Length);
        Assert.Equal("/orders/14", items[0].GetProperty("path").GetString());
        Assert.Equal("/orders/5", items[^1].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Concurrent_traffic_is_stored_without_loss_or_corruption()
    {
        await using var app = await TestApp.StartAsync(Dev());

        await Task.WhenAll(Enumerable.Range(0, 80).Select(i => app.Client.GetAsync($"/orders/{i}")));

        var items = Items(await Get(app, "/apipulse/api/requests?limit=200"));
        Assert.Equal(80, items.Length);
        Assert.Equal(80, items.Select(i => i.GetProperty("seq").GetInt64()).Distinct().Count());
    }

    [Fact]
    public async Task Dashboard_info_describes_the_service()
    {
        await using var app = await TestApp.StartAsync(Dev(o => o.ServiceName = "bookstore"));

        var info = await Get(app, "/apipulse/api/info");

        Assert.Equal("bookstore", info.GetProperty("service").GetString());
        Assert.Equal("Development", info.GetProperty("environment").GetString());
        Assert.Equal(1000, info.GetProperty("capacity").GetInt32());
    }
}

public class EventStoreTests
{
    private static EventStore Create(int capacity)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddApiPulse(o => o.Dashboard.MaxEntries = capacity);
        var host = builder.Build();
        return host.Services.GetRequiredService<EventStore>();
    }

    private static RequestEvent Event(string path) => new() { Path = path, Method = "GET" };

    [Fact]
    public void Entries_come_back_oldest_first_with_increasing_sequence_numbers()
    {
        var store = Create(10);

        store.Add(Event("/a"));
        store.Add(Event("/b"));

        var snapshot = store.Snapshot();
        Assert.Equal(new[] { "/a", "/b" }, snapshot.Select(e => e.Event.Path).ToArray());
        Assert.True(snapshot[0].Seq < snapshot[1].Seq);
    }

    [Fact]
    public void The_ring_overwrites_the_oldest_entries_when_full_and_sequence_numbers_keep_growing()
    {
        var store = Create(10);
        for (var i = 0; i < 25; i++)
        {
            store.Add(Event($"/{i}"));
        }

        var snapshot = store.Snapshot();
        Assert.Equal(10, snapshot.Length);
        Assert.Equal("/15", snapshot[0].Event.Path);
        Assert.Equal("/24", snapshot[^1].Event.Path);
        Assert.Equal(25, snapshot[^1].Seq);
    }

    [Fact]
    public void Clear_empties_the_store_but_keeps_sequence_numbers_monotonic()
    {
        var store = Create(10);
        store.Add(Event("/a"));
        var before = store.Snapshot()[0].Seq;

        store.Clear();
        store.Add(Event("/b"));

        var after = Assert.Single(store.Snapshot());
        Assert.True(after.Seq > before);
    }

    [Fact]
    public void Capacity_is_clamped_to_a_sane_range()
    {
        Assert.Equal(10, Create(1).Capacity);
        Assert.Equal(100_000, Create(int.MaxValue).Capacity);
    }
}
