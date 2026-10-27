using System.Collections.Concurrent;
using System.Security.Claims;
using ApiPulse.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPulse.Tests;

/// <summary>Collects everything the SDK exports, in memory.</summary>
internal sealed class CollectingSink : IApiPulseSink
{
    private readonly ConcurrentQueue<RequestEvent> _events = new();
    private readonly ConcurrentQueue<EventBatch> _batches = new();

    public bool IsEnabled { get; set; } = true;

    public IReadOnlyList<RequestEvent> Events => _events.ToArray();

    public IReadOnlyList<EventBatch> Batches => _batches.ToArray();

    public Task SendAsync(EventBatch batch, CancellationToken cancellationToken)
    {
        _batches.Enqueue(batch);
        foreach (var e in batch.Events)
        {
            _events.Enqueue(e);
        }

        return Task.CompletedTask;
    }
}

internal sealed class ReloadableConfigSource : IConfigurationSource
{
    private readonly Provider _provider = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;

    public void Set(string key, string? value) => _provider.Update(key, value);

    private sealed class Provider : ConfigurationProvider
    {
        public void Update(string key, string? value)
        {
            Data[key] = value;
            OnReload();
        }
    }
}

internal sealed class FakeEnvironment : IEnvironmentReader
{
    private readonly Dictionary<string, string> _values;

    public FakeEnvironment(Dictionary<string, string>? values = null) => _values = values ?? new();

    public string? Get(string name) => _values.GetValueOrDefault(name);
}

/// <summary>An in-memory logger provider, standing in for console logging in assertions about what the application logs.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}

internal sealed class TestAppSetup
{
    public Action<ApiPulseOptions>? Configure { get; set; }

    public Dictionary<string, string?>? Config { get; set; }

    public Dictionary<string, string>? Env { get; set; }

    public Action<IServiceCollection>? Services { get; set; }

    public Action<WebApplication>? Pipeline { get; set; }

    public bool UseExceptionHandler { get; set; }

    public bool UseDeveloperExceptionPage { get; set; }

    /// <summary>The hosting environment. The dashboard is on by default only in <c>Development</c>.</summary>
    public string Environment { get; set; } = "Testing";

    public bool WithSink { get; set; } = true;

    public bool CallUseApiPulse { get; set; }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

public sealed record OrderDto(string Customer, string Password);

[ApiController]
[Route("api/items")]
public sealed class ItemsController(ILogger<ItemsController> logger) : ControllerBase
{
    [HttpGet("{id:int}")]
    public IActionResult Get(int id)
    {
        logger.LogInformation("Controller loading item {ItemId}", id);
        return Ok(new { id, name = "widget" });
    }

    [HttpGet("hidden")]
    [ExcludeFromApiPulse]
    public IActionResult Hidden() => Ok("hidden");

    [HttpGet("fail")]
    public IActionResult Fail() => throw new InvalidOperationException("controller failure");
}

/// <summary>Hosts a minimal ASP.NET Core application on <c>TestServer</c> with the SDK installed and a collecting sink attached.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private TestApp(WebApplication app, CollectingSink sink, ListLoggerProvider logs)
    {
        App = app;
        Sink = sink;
        AppLogs = logs;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public CollectingSink Sink { get; }

    public ListLoggerProvider AppLogs { get; }

    public IApiPulseExporter Exporter => App.Services.GetRequiredService<IApiPulseExporter>();

    public static async Task<TestApp> StartAsync(TestAppSetup? setup = null)
    {
        setup ??= new TestAppSetup();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = setup.Environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(setup.MinimumLevel);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning); // what the default project template configures
        var appLogs = new ListLoggerProvider();
        builder.Logging.AddProvider(appLogs);

        if (setup.Config is not null)
        {
            builder.Configuration.AddInMemoryCollection(setup.Config);
        }

        var reloadable = new ReloadableConfigSource();
        ((IConfigurationBuilder)builder.Configuration).Add(reloadable);

        var sink = new CollectingSink { IsEnabled = setup.WithSink };
        builder.Services.AddSingleton<IEnvironmentReader>(new FakeEnvironment(setup.Env));
        builder.Services.AddApiPulse(setup.Configure);

        if (setup.WithSink)
        {
            builder.Services.AddSingleton<IApiPulseSink>(sink);
        }

        builder.Services.AddControllers().AddApplicationPart(typeof(ItemsController).Assembly);
        setup.Services?.Invoke(builder.Services);

        var app = builder.Build();
        if (setup.UseExceptionHandler)
        {
            app.UseExceptionHandler(handler => handler.Run(async ctx =>
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("handled");
            }));
        }

        if (setup.UseDeveloperExceptionPage)
        {
            app.UseDeveloperExceptionPage();
        }

        if (setup.CallUseApiPulse)
        {
            app.UseApiPulse();
        }

        MapEndpoints(app);
        setup.Pipeline?.Invoke(app);

        await app.StartAsync();
        return new TestApp(app, sink, appLogs) { Reloadable = reloadable };
    }

    /// <summary>A configuration source whose values can be changed after startup, raising the reload token like appsettings.json does.</summary>
    public ReloadableConfigSource Reloadable { get; private init; } = null!;

    /// <summary>Flushes the export queue and returns every event delivered so far.</summary>
    public async Task<IReadOnlyList<RequestEvent>> EventsAsync()
    {
        await Exporter.FlushAsync();
        return Sink.Events;
    }

    public async Task<RequestEvent> SingleEventAsync()
    {
        var events = await EventsAsync();
        return Assert.Single(events);
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/orders/{id:int}", async (int id, ILogger<TestApp> logger) =>
        {
            logger.LogInformation("Loading order {OrderId}", id);
            await Task.Delay(Random.Shared.Next(1, 15));
            logger.LogInformation("Loaded order {OrderId}", id);
            return Results.Json(new { id, status = "shipped" });
        });

        app.MapPost("/orders", (OrderDto dto) => Results.Json(new { id = 42, customer = dto.Customer, token = "response-secret" }));

        app.MapPost("/echo", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync();
            return Results.Text(text, request.ContentType ?? "text/plain");
        });

        app.MapGet("/big", () => Results.Text(new string('x', 100_000), "text/plain"));
        app.MapGet("/binary", () => Results.Bytes(new byte[2048], "application/octet-stream"));
        app.MapGet("/search", () => Results.Ok("ok"));
        app.MapGet("/health", () => Results.Ok("healthy"));
        app.MapGet("/health/live", () => Results.Ok("live"));
        app.MapGet("/internal/status", () => Results.Ok("internal"));
        app.MapGet("/boom", (ILogger<TestApp> logger) =>
        {
            logger.LogInformation("about to fail");
            throw new InvalidOperationException("kaboom");
        });
        app.MapGet("/hidden", () => Results.Ok("hidden")).ExcludeFromApiPulse();
        app.MapGroup("/group").ExcludeFromApiPulse().MapGet("/inner", () => Results.Ok("inner"));

        app.MapGet("/ctx", (IApiPulseContext pulse, HttpContext http) =>
        {
            pulse.SetConsumer("acme-42", "ACME Corp", "enterprise");
            pulse.SetTag("tenant", "acme");
            http.SetApiPulseTag("feature", "beta");
            return Results.Ok(new { active = pulse.IsActive, trace = pulse.TraceId });
        });

        app.MapGet("/logs", (ILoggerFactory factory, ILogger<TestApp> logger) =>
        {
            logger.LogDebug("debug detail");
            logger.LogInformation("info detail for {Password} and {UserName}", "hunter2", "bob");
            logger.LogWarning("warning detail with Bearer abc.def.ghi inside");
            factory.CreateLogger("ApiPulse.Exporter").LogWarning("own category must be ignored");
            return Results.Ok();
        });

        app.MapGet("/log-flood", (ILogger<TestApp> logger) =>
        {
            for (var i = 0; i < 10; i++)
            {
                logger.LogInformation("entry {Index}", i);
            }

            return Results.Ok();
        });

        app.MapGet("/log-exception", (ILogger<TestApp> logger, IApiPulseContext pulse) =>
        {
            var ex = new TimeoutException("upstream timed out");
            pulse.RecordException(ex);
            logger.LogError(ex, "upstream call failed");
            return Results.Ok();
        });

        app.MapGet("/background-log", async (ILogger<TestApp> logger) =>
        {
            await Task.Run(() => logger.LogInformation("from a task started by the request"));
            return Results.Ok();
        });

        app.MapGet("/spans", async () =>
        {
            using (var span = ApiPulseActivitySource.Instance.StartActivity("db.query", System.Diagnostics.ActivityKind.Client))
            {
                span?.SetTag("db.system", "postgresql");
                span?.SetTag("db.statement", "SELECT * FROM users WHERE email = 'a@b.c'");
                span?.SetTag("url.full", "https://api.example.com/v1/x?token=abc&page=2");
                await Task.Delay(5);
            }

            return Results.Ok();
        });

        app.MapGet("/keep", (IApiPulseContext pulse) =>
        {
            pulse.Keep();
            return Results.Ok();
        });

        app.MapGet("/suppress", (IApiPulseContext pulse) =>
        {
            pulse.Suppress();
            return Results.Ok();
        });

        app.MapControllers();
    }

    public static ClaimsPrincipal User(string id, string name) =>
        new(new ClaimsIdentity(new[] { new Claim("sub", id), new Claim(ClaimTypes.Name, name) }, "test"));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
