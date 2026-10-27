using ApiPulse.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ApiPulse.Tests;

public class ConfigurationTests
{
    private static ApiPulseOptions OptionsOf(TestApp app) => app.App.Services.GetRequiredService<IOptions<ApiPulseOptions>>().Value;

    [Fact]
    public async Task Defaults_are_production_ready()
    {
        await using var app = await TestApp.StartAsync();

        var o = OptionsOf(app);
        Assert.True(o.Enabled);
        Assert.True(o.AutoRegisterMiddleware);
        Assert.Null(o.CaptureRequestBody); // automatic: only in Development with the dashboard on
        Assert.Null(o.CaptureResponseBody);
        Assert.Equal(1.0, o.SamplingRate);
        Assert.True(o.AlwaysCaptureErrors);
        Assert.Contains("/health", o.ExcludedPaths);
        Assert.Contains("Authorization", o.MaskHeaders);
        Assert.Contains("*password*", o.MaskBodyFields);
        Assert.Equal(LogLevel.Information, o.MinimumLogLevel);
        Assert.Equal(10_000, o.MaxQueueSize);
    }

    [Fact]
    public async Task Appsettings_section_is_bound_and_lists_are_additive()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Config = new()
            {
                ["ApiPulse:ServiceName"] = "from-appsettings",
                ["ApiPulse:SamplingRate"] = "0.25",
                ["ApiPulse:CaptureRequestBody"] = "true",
                ["ApiPulse:MinimumLogLevel"] = "Warning",
                ["ApiPulse:FlushInterval"] = "00:00:30",
                ["ApiPulse:ClientIp"] = "Anonymized",
                ["ApiPulse:Endpoint"] = "https://ingest.example.com/v1/events",
                ["ApiPulse:MaskHeaders:0"] = "X-Internal-Secret-Thing",
                ["ApiPulse:ExcludedPaths:0"] = "/internal/*",
            },
        });

        var o = OptionsOf(app);
        Assert.Equal("from-appsettings", o.ServiceName);
        Assert.Equal(0.25, o.SamplingRate);
        Assert.True(o.CaptureRequestBody);
        Assert.Equal(LogLevel.Warning, o.MinimumLogLevel);
        Assert.Equal(TimeSpan.FromSeconds(30), o.FlushInterval);
        Assert.Equal(ClientIpMode.Anonymized, o.ClientIp);
        Assert.Equal(new Uri("https://ingest.example.com/v1/events"), o.Endpoint);
        Assert.Contains("X-Internal-Secret-Thing", o.MaskHeaders);
        Assert.Contains("Authorization", o.MaskHeaders); // defaults kept
        Assert.Contains("/internal/*", o.ExcludedPaths);
        Assert.Contains("/health", o.ExcludedPaths);
    }

    [Fact]
    public async Task Double_underscore_environment_style_keys_work_through_IConfiguration()
    {
        // ApiPulse__SamplingRate=0.3 reaches the app as the configuration key ApiPulse:SamplingRate.
        await using var app = await TestApp.StartAsync(new TestAppSetup { Config = new() { ["ApiPulse:SamplingRate"] = "0.3" } });

        Assert.Equal(0.3, OptionsOf(app).SamplingRate);
    }

    [Fact]
    public async Task Short_environment_variables_override_appsettings_which_override_code()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.SamplingRate = 1.0; o.ServiceName = "code"; o.ApiKey = "code-key"; },
            Config = new() { ["ApiPulse:SamplingRate"] = "0.5", ["ApiPulse:ServiceName"] = "appsettings" },
            Env = new()
            {
                ["APIPULSE_SAMPLING_RATE"] = "0.1",
                ["APIPULSE_API_KEY"] = "env-key",
                ["APIPULSE_ENDPOINT"] = "https://env.example.com/ingest",
                ["APIPULSE_ENVIRONMENT"] = "staging",
                ["APIPULSE_SERVICE_VERSION"] = "9.9.9",
                ["APIPULSE_CAPTURE_REQUEST_BODY"] = "1",
                ["APIPULSE_CAPTURE_RESPONSE_BODY"] = "true",
                ["APIPULSE_CAPTURE_LOGS"] = "false",
                ["APIPULSE_MIN_LOG_LEVEL"] = "error",
                ["APIPULSE_LOG_EVENTS"] = "true",
            },
        });

        var o = OptionsOf(app);
        Assert.Equal(0.1, o.SamplingRate); // env > appsettings (0.5) > code (1.0)
        Assert.Equal("appsettings", o.ServiceName); // appsettings > code
        Assert.Equal("env-key", o.ApiKey);
        Assert.Equal(new Uri("https://env.example.com/ingest"), o.Endpoint);
        Assert.Equal("staging", o.EnvironmentName);
        Assert.Equal("9.9.9", o.ServiceVersion);
        Assert.True(o.CaptureRequestBody);
        Assert.True(o.CaptureResponseBody);
        Assert.False(o.CaptureLogs);
        Assert.Equal(LogLevel.Error, o.MinimumLogLevel);
        Assert.True(o.LogEvents);
    }

    [Fact]
    public async Task Environment_lists_add_to_defaults_without_duplicates()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Env = new()
            {
                ["APIPULSE_EXCLUDED_PATHS"] = "/internal/*, /health ,/extra",
                ["APIPULSE_MASK_HEADERS"] = "X-Tenant-Secret",
                ["APIPULSE_MASK_QUERY_PARAMS"] = "ssn",
                ["APIPULSE_MASK_BODY_FIELDS"] = "nationalId",
            },
        });

        var o = OptionsOf(app);
        Assert.Contains("/internal/*", o.ExcludedPaths);
        Assert.Contains("/extra", o.ExcludedPaths);
        Assert.Single(o.ExcludedPaths, p => p == "/health");
        Assert.Contains("X-Tenant-Secret", o.MaskHeaders);
        Assert.Contains("ssn", o.MaskQueryParams);
        Assert.Contains("nationalId", o.MaskBodyFields);

        await app.Client.GetAsync("/internal/status");
        await app.Client.GetAsync("/search");
        Assert.Equal("/search", Assert.Single(await app.EventsAsync()).Path);
    }

    [Fact]
    public async Task APIPULSE_ENABLED_false_switches_the_sdk_off_even_when_code_enables_it()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.Enabled = true,
            Env = new() { ["APIPULSE_ENABLED"] = "false" },
        });

        await app.Client.GetAsync("/search");

        Assert.Empty(await app.EventsAsync());
    }

    [Fact]
    public async Task Unparseable_environment_values_are_ignored()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Env = new()
            {
                ["APIPULSE_SAMPLING_RATE"] = "lots",
                ["APIPULSE_ENABLED"] = "maybe",
                ["APIPULSE_ENDPOINT"] = "not a url",
                ["APIPULSE_MIN_LOG_LEVEL"] = "loud",
            },
        });

        var o = OptionsOf(app);
        Assert.Equal(1.0, o.SamplingRate);
        Assert.True(o.Enabled);
        Assert.Null(o.Endpoint);
        Assert.Equal(LogLevel.Information, o.MinimumLogLevel);
    }

    [Fact]
    public async Task Out_of_range_values_are_clamped_and_never_prevent_startup()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o =>
            {
                o.SamplingRate = 7;
                o.MaxBodySize = -5;
                o.BatchSize = 0;
                o.MaxQueueSize = 0;
                o.FlushInterval = TimeSpan.Zero;
                o.MaxRetries = 999;
                o.Endpoint = new Uri("/relative", UriKind.Relative);
            },
        });

        var settings = app.App.Services.GetRequiredService<ApiPulseRuntime>().Current;
        Assert.Equal(1.0, settings.SamplingRate);
        Assert.Equal(0, settings.MaxBodySize);
        Assert.Equal(1, settings.BatchSize);
        Assert.Equal(1, settings.MaxQueueSize);
        Assert.True(settings.FlushInterval > TimeSpan.Zero);
        Assert.Equal(10, settings.MaxRetries);
        Assert.Null(settings.Endpoint);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await app.Client.GetAsync("/search")).StatusCode);
    }

    [Fact]
    public async Task Settings_reload_at_runtime_when_configuration_changes()
    {
        await using var app = await TestApp.StartAsync();
        await app.Client.GetAsync("/search");

        app.Reloadable.Set("ApiPulse:Enabled", "false");
        await app.Client.GetAsync("/search");

        app.Reloadable.Set("ApiPulse:Enabled", "true");
        await app.Client.GetAsync("/search");

        Assert.Equal(2, (await app.EventsAsync()).Count);
    }

    [Fact]
    public async Task Calling_AddApiPulse_twice_does_not_duplicate_capture_or_export()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Services = s => s.AddApiPulse(o => o.ServiceName = "second-call"),
        });

        await app.Client.GetAsync("/search");

        Assert.Single(await app.EventsAsync());
        Assert.Equal("second-call", OptionsOf(app).ServiceName);
        Assert.Single(app.App.Services.GetServices<IHostedService>().OfType<ApiPulseExporter>());
    }

    [Fact]
    public async Task Middleware_registration_can_be_manual()
    {
        await using var manual = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.AutoRegisterMiddleware = false,
            CallUseApiPulse = true,
        });
        await using var forgotten = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.AutoRegisterMiddleware = false,
        });

        await manual.Client.GetAsync("/search");
        await forgotten.Client.GetAsync("/search");

        Assert.Single(await manual.EventsAsync());
        Assert.Empty(await forgotten.EventsAsync());
    }

    [Fact]
    public async Task Calling_UseApiPulse_in_addition_to_the_automatic_registration_is_harmless()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup { CallUseApiPulse = true });

        await app.Client.GetAsync("/search");

        Assert.Single(await app.EventsAsync());
    }

    [Fact]
    public async Task UseApiPulse_without_AddApiPulse_explains_what_is_missing()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.UseApiPulse());

        Assert.Contains("AddApiPulse", ex.Message);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Builder_extension_registers_the_same_services()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddApiPulse(o => o.ServiceName = "via-builder");

        await using var app = builder.Build();
        Assert.Equal("via-builder", app.Services.GetRequiredService<IOptions<ApiPulseOptions>>().Value.ServiceName);
    }

    [Fact]
    public async Task Service_identity_defaults_come_from_the_host()
    {
        await using var app = await TestApp.StartAsync();

        var service = app.App.Services.GetRequiredService<ApiPulseRuntime>().Current.Service;

        Assert.False(string.IsNullOrWhiteSpace(service.Name));
        Assert.Equal("Testing", service.Environment);
        Assert.Equal(Environment.MachineName, service.Instance);
    }
}
