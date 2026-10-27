using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPulse.Internal;

/// <summary>
/// An immutable, pre-compiled view of <see cref="ApiPulseOptions"/>: matchers are built and numeric settings are clamped once
/// per configuration change instead of once per request. Invalid values are corrected rather than rejected, because a monitoring
/// SDK must never prevent the application from starting.
/// </summary>
internal sealed class ApiPulseSettings
{
    public ApiPulseSettings(ApiPulseOptions options, IHostEnvironment environment, ILogger? logger)
    {
        Options = options;
        Masker = new Masker(options, logger);
        var dashboardPath = "/" + (options.Dashboard.Path ?? string.Empty).Trim().Trim('/');
        DashboardPath = dashboardPath == "/" ? "/apipulse" : dashboardPath;
        DashboardMaxEntries = Math.Clamp(options.Dashboard.MaxEntries, 10, 100_000);
        IsDevelopment = environment.IsDevelopment();
        HostEnvironmentName = environment.EnvironmentName;

        // The dashboard only runs where it is safe to: Development, or wherever the application supplied an access rule.
        DashboardActive = options.Enabled && options.Dashboard.Enabled && (options.Dashboard.Authorize is not null || IsDevelopment);

        // Bodies are captured automatically only where the developer is looking at them: Development with the dashboard on.
        // Anything the application set explicitly wins, in either direction.
        var autoBodies = DashboardActive && IsDevelopment;
        CaptureRequestBody = options.CaptureRequestBody ?? autoBodies;
        CaptureResponseBody = options.CaptureResponseBody ?? autoBodies;

        // The dashboard's own traffic must never show up in itself.
        ExcludedPaths = new PathMatcher(options.ExcludedPaths.Concat(new[] { DashboardPath, DashboardPath + "/*" }));
        ExcludedMethods = new HashSet<string>(options.ExcludedMethods.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()), StringComparer.OrdinalIgnoreCase);
        BodyContentTypes = new NameMatcher(options.BodyContentTypes);

        var sources = new List<string>(options.SpanSources) { ApiPulseDefaults.ActivitySourceName };
        SpanSources = new NameMatcher(sources);

        SamplingRate = double.IsNaN(options.SamplingRate) ? 1.0 : Math.Clamp(options.SamplingRate, 0.0, 1.0);
        MaxBodySize = Math.Clamp(options.MaxBodySize, 0, 1024 * 1024);
        MaxLogsPerRequest = Math.Max(0, options.MaxLogsPerRequest);
        MaxLogMessageLength = Math.Max(16, options.MaxLogMessageLength);
        MaxSpansPerRequest = Math.Max(0, options.MaxSpansPerRequest);
        MinimumLogLevel = options.MinimumLogLevel == LogLevel.None ? LogLevel.Critical : options.MinimumLogLevel;
        BatchSize = Math.Max(1, options.BatchSize);
        MaxQueueSize = Math.Max(1, options.MaxQueueSize);
        FlushInterval = options.FlushInterval > TimeSpan.Zero ? options.FlushInterval : TimeSpan.FromSeconds(5);
        ExportTimeout = options.ExportTimeout > TimeSpan.Zero ? options.ExportTimeout : TimeSpan.FromSeconds(10);
        ShutdownTimeout = options.ShutdownTimeout > TimeSpan.Zero ? options.ShutdownTimeout : TimeSpan.FromSeconds(5);
        MaxRetries = Math.Clamp(options.MaxRetries, 0, 10);

        if (options.Endpoint is { IsAbsoluteUri: false } or { IsAbsoluteUri: true, Scheme: not ("http" or "https") })
        {
            logger?.LogWarning("ApiPulse endpoint '{Endpoint}' is not an absolute http(s) URL and will be ignored.", options.Endpoint);
            Endpoint = null;
        }
        else
        {
            Endpoint = options.Endpoint;
        }

        Service = new ServiceInfo
        {
            Name = options.ServiceName ?? environment.ApplicationName,
            Version = options.ServiceVersion ?? EntryAssemblyVersion(),
            Environment = options.EnvironmentName ?? environment.EnvironmentName,
            Instance = Environment.MachineName,
        };
    }

    public ApiPulseOptions Options { get; }

    public string DashboardPath { get; }

    public int DashboardMaxEntries { get; }

    public bool DashboardActive { get; }

    public bool IsDevelopment { get; }

    /// <summary>The hosting environment (<c>ASPNETCORE_ENVIRONMENT</c>), as opposed to the label sent with exported events.</summary>
    public string HostEnvironmentName { get; }

    /// <summary>The effective value: the explicit option, or the Development-with-dashboard default.</summary>
    public bool CaptureRequestBody { get; }

    /// <summary>The effective value: the explicit option, or the Development-with-dashboard default.</summary>
    public bool CaptureResponseBody { get; }

    public Masker Masker { get; }

    public PathMatcher ExcludedPaths { get; }

    public HashSet<string> ExcludedMethods { get; }

    public NameMatcher BodyContentTypes { get; }

    public NameMatcher SpanSources { get; }

    public double SamplingRate { get; }

    public int MaxBodySize { get; }

    public int MaxLogsPerRequest { get; }

    public int MaxLogMessageLength { get; }

    public int MaxSpansPerRequest { get; }

    public LogLevel MinimumLogLevel { get; }

    public int BatchSize { get; }

    public int MaxQueueSize { get; }

    public TimeSpan FlushInterval { get; }

    public TimeSpan ExportTimeout { get; }

    public TimeSpan ShutdownTimeout { get; }

    public int MaxRetries { get; }

    public Uri? Endpoint { get; }

    public ServiceInfo Service { get; }

    public static SdkInfo Sdk { get; } = new()
    {
        Version = typeof(ApiPulseSettings).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0",
        Runtime = RuntimeInformation.FrameworkDescription,
    };

    private static string? EntryAssemblyVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
}

/// <summary>Holds the current <see cref="ApiPulseSettings"/> and rebuilds it whenever the options change.</summary>
internal sealed class ApiPulseRuntime : IDisposable
{
    private readonly IOptionsMonitor<ApiPulseOptions> _monitor;
    private readonly IHostEnvironment _environment;
    private readonly IServiceProvider _services;
    private readonly IDisposable? _subscription;
    private volatile ApiPulseSettings? _current;

    public ApiPulseRuntime(IOptionsMonitor<ApiPulseOptions> monitor, IHostEnvironment environment, IServiceProvider services)
    {
        _monitor = monitor;
        _environment = environment;
        _services = services;
        _subscription = monitor.OnChange(_ => _current = null);
    }

    /// <summary>The settings in effect. Cheap: a volatile read once built.</summary>
    public ApiPulseSettings Current => _current ??= Build();

    private ApiPulseSettings Build()
    {
        // The logger factory is resolved lazily (not injected) because the SDK's own logger provider is part of it.
        var logger = _services.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
            ? factory.CreateLogger("ApiPulse.Configuration")
            : null;
        return new ApiPulseSettings(_monitor.CurrentValue, _environment, logger);
    }

    public void Dispose() => _subscription?.Dispose();
}
