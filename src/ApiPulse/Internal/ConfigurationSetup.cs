using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPulse.Internal;

/// <summary>Binds the <c>ApiPulse</c> configuration section (appsettings.json, <c>ApiPulse__*</c> variables, user secrets...).</summary>
internal sealed class ConfigurationOptionsSetup : IConfigureOptions<ApiPulseOptions>
{
    private readonly IConfiguration _configuration;

    public ConfigurationOptionsSetup(IConfiguration configuration) => _configuration = configuration;

    public void Configure(ApiPulseOptions options) => _configuration.GetSection(ApiPulseOptions.SectionName).Bind(options);
}

/// <summary>Lets <c>IOptionsMonitor</c> notice configuration reloads, so settings such as <c>Enabled</c> or <c>SamplingRate</c> can change at runtime.</summary>
internal sealed class ConfigurationChangeTokenSource : IOptionsChangeTokenSource<ApiPulseOptions>
{
    private readonly IConfiguration _configuration;

    public ConfigurationChangeTokenSource(IConfiguration configuration) => _configuration = configuration;

    public string? Name => Options.DefaultName;

    public Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() => _configuration.GetSection(ApiPulseOptions.SectionName).GetReloadToken();
}

/// <summary>Abstraction over environment variables so tests do not have to mutate process state.</summary>
internal interface IEnvironmentReader
{
    string? Get(string name);
}

internal sealed class EnvironmentReader : IEnvironmentReader
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>
/// Applies the short <c>APIPULSE_*</c> environment variables. Registered last, so these are the highest-precedence source:
/// they are meant for operators overriding behaviour at deployment time. Unparseable values are ignored.
/// </summary>
internal sealed class EnvironmentOptionsSetup : IConfigureOptions<ApiPulseOptions>
{
    private readonly IEnvironmentReader _env;

    public EnvironmentOptionsSetup(IEnvironmentReader env) => _env = env;

    public void Configure(ApiPulseOptions options)
    {
        if (Bool("APIPULSE_ENABLED") is { } enabled)
        {
            options.Enabled = enabled;
        }

        if (Text("APIPULSE_API_KEY") is { } key)
        {
            options.ApiKey = key;
        }

        if (Text("APIPULSE_ENDPOINT") is { } endpoint && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            options.Endpoint = uri;
        }

        if (Text("APIPULSE_SERVICE_NAME") is { } name)
        {
            options.ServiceName = name;
        }

        if (Text("APIPULSE_SERVICE_VERSION") is { } version)
        {
            options.ServiceVersion = version;
        }

        if (Text("APIPULSE_ENVIRONMENT") is { } environment)
        {
            options.EnvironmentName = environment;
        }

        if (Text("APIPULSE_SAMPLING_RATE") is { } rate
            && double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate))
        {
            options.SamplingRate = parsedRate;
        }

        if (Bool("APIPULSE_CAPTURE_REQUEST_BODY") is { } requestBody)
        {
            options.CaptureRequestBody = requestBody;
        }

        if (Bool("APIPULSE_CAPTURE_RESPONSE_BODY") is { } responseBody)
        {
            options.CaptureResponseBody = responseBody;
        }

        if (Bool("APIPULSE_CAPTURE_LOGS") is { } logs)
        {
            options.CaptureLogs = logs;
        }

        if (Bool("APIPULSE_LOG_EVENTS") is { } logEvents)
        {
            options.LogEvents = logEvents;
        }

        if (Bool("APIPULSE_DASHBOARD") is { } dashboard)
        {
            options.Dashboard.Enabled = dashboard;
        }

        if (Text("APIPULSE_DASHBOARD_PATH") is { } dashboardPath)
        {
            options.Dashboard.Path = dashboardPath;
        }

        if (Text("APIPULSE_MIN_LOG_LEVEL") is { } level && Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsedLevel))
        {
            options.MinimumLogLevel = parsedLevel;
        }

        AddAll("APIPULSE_EXCLUDED_PATHS", options.ExcludedPaths);
        AddAll("APIPULSE_MASK_HEADERS", options.MaskHeaders);
        AddAll("APIPULSE_MASK_QUERY_PARAMS", options.MaskQueryParams);
        AddAll("APIPULSE_MASK_BODY_FIELDS", options.MaskBodyFields);
    }

    private string? Text(string name) => _env.Get(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private bool? Bool(string name) => Text(name) switch
    {
        null => null,
        "1" => true,
        "0" => false,
        { } v when bool.TryParse(v, out var b) => b,
        _ => null,
    };

    private void AddAll(string name, IList<string> target)
    {
        if (Text(name) is not { } list)
        {
            return;
        }

        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!target.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(item);
            }
        }
    }
}

/// <summary>Inserts the middleware as the outermost component of the pipeline so no <c>UseApiPulse()</c> call is required.</summary>
internal sealed class ApiPulseStartupFilter : IStartupFilter
{
    private readonly IOptions<ApiPulseOptions> _options;

    public ApiPulseStartupFilter(IOptions<ApiPulseOptions> options) => _options = options;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (_options.Value.AutoRegisterMiddleware)
        {
            app.UseMiddleware<ApiPulseMiddleware>();
        }

        app.UseMiddleware<DashboardMiddleware>();
        next(app);
    };
}
