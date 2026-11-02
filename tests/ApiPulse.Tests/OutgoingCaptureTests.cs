using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace ApiPulse.Tests;

/// <summary>
/// Outgoing calls need a real socket: the HTTP activity is started by the runtime's own handler, which an in-memory test server bypasses.
/// </summary>
public sealed class OutgoingCaptureTests : IAsyncLifetime
{
    private WebApplication _upstream = null!;

    private string Upstream => _upstream.Urls.First();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _upstream = builder.Build();

        _upstream.MapPost("/echo", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var received = await reader.ReadToEndAsync();
            return Results.Json(new { received, token = "upstream-secret", price = 12.5 });
        });
        _upstream.MapGet("/big", () => Results.Text(new string('x', 100_000), "text/plain"));
        _upstream.MapGet("/binary", () => Results.Bytes(new byte[2048], "application/octet-stream"));

        await _upstream.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _upstream.StopAsync();
        await _upstream.DisposeAsync();
    }

    private const string OrderJson = """{"isbn":"978-0","password":"hunter2"}""";

    private static void MapCall(WebApplication app, Func<HttpClient, Task<string>> call) =>
        app.MapGet("/call", async (IHttpClientFactory factory) => Results.Text(await call(factory.CreateClient()), "text/plain"));

    private async Task<string> PostOrderAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Upstream + "/echo")
        {
            Content = new StringContent(OrderJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer abc.def.ghi");
        request.Headers.Add("X-Trace-Note", "visible");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private TestAppSetup Setup(Func<HttpClient, Task<string>> call, Action<ApiPulseOptions>? configure = null) => new()
    {
        Configure = configure,
        Pipeline = app => MapCall(app, call),
    };

    [Fact]
    public async Task Request_and_response_of_an_outgoing_call_are_attached_to_its_span_and_masked()
    {
        await using var app = await TestApp.StartAsync(Setup(PostOrderAsync, o => o.CaptureOutgoingBodies = true));

        var answer = await app.Client.GetStringAsync("/call");

        var span = Assert.Single((await app.SingleEventAsync()).Spans!);
        Assert.Contains("\"received\"", answer); // the application got the real response, untouched
        Assert.Contains("hunter2", answer);

        Assert.Equal("visible", span.RequestHeaders!["X-Trace-Note"]);
        Assert.Equal("******", span.RequestHeaders["Authorization"]);
        Assert.Contains("application/json", span.RequestHeaders["Content-Type"]);
        Assert.Contains("application/json", span.ResponseHeaders!["Content-Type"]);

        Assert.Contains("\"isbn\":\"978-0\"", span.RequestBody);
        Assert.DoesNotContain("hunter2", span.RequestBody);
        Assert.Contains("\"price\":12.5", span.ResponseBody);
        Assert.DoesNotContain("upstream-secret", span.ResponseBody);
    }

    [Fact]
    public async Task Bodies_are_off_by_default_outside_development_but_headers_are_kept()
    {
        await using var app = await TestApp.StartAsync(Setup(PostOrderAsync));

        await app.Client.GetStringAsync("/call");

        var span = Assert.Single((await app.SingleEventAsync()).Spans!);
        Assert.Null(span.RequestBody);
        Assert.Null(span.ResponseBody);
        Assert.NotNull(span.RequestHeaders);
        Assert.NotNull(span.ResponseHeaders);
    }

    [Fact]
    public async Task Bodies_are_captured_automatically_in_development()
    {
        var setup = Setup(PostOrderAsync);
        setup.Environment = "Development";
        await using var app = await TestApp.StartAsync(setup);

        await app.Client.GetStringAsync("/call");

        var span = Assert.Single((await app.SingleEventAsync()).Spans!);
        Assert.NotNull(span.RequestBody);
        Assert.NotNull(span.ResponseBody);
    }

    [Fact]
    public async Task Headers_follow_the_existing_header_switches()
    {
        await using var app = await TestApp.StartAsync(Setup(PostOrderAsync, o =>
        {
            o.CaptureRequestHeaders = false;
            o.CaptureResponseHeaders = false;
        }));

        await app.Client.GetStringAsync("/call");

        var span = Assert.Single((await app.SingleEventAsync()).Spans!);
        Assert.Null(span.RequestHeaders);
        Assert.Null(span.ResponseHeaders);
    }

    [Fact]
    public async Task Large_bodies_are_truncated_without_changing_what_the_application_reads()
    {
        string? received = null;
        await using var app = await TestApp.StartAsync(Setup(async client =>
        {
            received = await client.GetStringAsync(Upstream + "/big");
            return "done";
        }, o =>
        {
            o.CaptureOutgoingBodies = true;
            o.MaxBodySize = 100;
        }));

        await app.Client.GetStringAsync("/call");

        var span = Assert.Single((await app.SingleEventAsync()).Spans!);
        Assert.Equal(100_000, received!.Length);
        Assert.Equal(100, span.ResponseBody!.Length);
        Assert.True(span.ResponseBodyTruncated);
    }

    [Fact]
    public async Task Binary_responses_are_not_captured()
    {
        byte[]? received = null;
        await using var app = await TestApp.StartAsync(Setup(async client =>
        {
            received = await client.GetByteArrayAsync(Upstream + "/binary");
            return "done";
        }, o => o.CaptureOutgoingBodies = true));

        await app.Client.GetStringAsync("/call");

        var span = Assert.Single((await app.SingleEventAsync()).Spans!);
        Assert.Equal(2048, received!.Length);
        Assert.Null(span.ResponseBody);
    }

    [Fact]
    public async Task Streamed_responses_are_captured_as_the_application_reads_them()
    {
        string? streamed = null;
        await using var app = await TestApp.StartAsync(Setup(async client =>
        {
            using var response = await client.GetAsync(Upstream + "/big", HttpCompletionOption.ResponseHeadersRead);
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            streamed = await reader.ReadToEndAsync();
            return "done";
        }, o =>
        {
            o.CaptureOutgoingBodies = true;
            o.MaxBodySize = 64;
        }));

        await app.Client.GetStringAsync("/call");

        var span = Assert.Single((await app.SingleEventAsync()).Spans!);
        Assert.Equal(100_000, streamed!.Length);
        Assert.Equal(64, span.ResponseBody!.Length);
    }

    [Fact]
    public async Task Nothing_is_captured_for_unsampled_requests()
    {
        await using var app = await TestApp.StartAsync(Setup(PostOrderAsync, o =>
        {
            o.CaptureOutgoingBodies = true;
            o.SamplingRate = 0;
        }));

        await app.Client.GetStringAsync("/call");

        Assert.Empty(await app.EventsAsync()); // dropped entirely: not an error and not slow
    }

    [Fact]
    public async Task Calls_made_with_a_bare_HttpClient_still_produce_a_span_without_payloads()
    {
        await using var app = await TestApp.StartAsync(Setup(async _ =>
        {
            using var client = new HttpClient();
            return await client.GetStringAsync(Upstream + "/big");
        }, o => o.CaptureOutgoingBodies = true));

        await app.Client.GetStringAsync("/call");

        var span = Assert.Single((await app.SingleEventAsync()).Spans!);
        Assert.Null(span.RequestHeaders);
        Assert.Null(span.ResponseBody);
    }
}
