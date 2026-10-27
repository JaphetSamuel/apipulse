using ApiPulse.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPulse;

/// <summary>Service registration for ApiPulse.</summary>
public static class ApiPulseServiceCollectionExtensions
{
    /// <summary>
    /// Adds API monitoring. This single call is all that is needed: it registers the middleware (inserted automatically at the top
    /// of the pipeline), the <c>ILogger</c> provider that correlates logs with requests, span collection, metrics and the
    /// background exporter.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Optional code configuration. Values from the <c>ApiPulse</c> configuration section and from <c>APIPULSE_*</c> environment
    /// variables are applied after this callback and therefore win; see <see cref="ApiPulseOptions"/>.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddApiPulse(o =>
    /// {
    ///     o.ApiKey = "your-key";
    ///     o.Endpoint = new Uri("https://ingest.example.com/v1/events");
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddApiPulse(this IServiceCollection services, Action<ApiPulseOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }

        // Registering twice is harmless: only the extra configure callback (above) is applied.
        if (services.Any(d => d.ServiceType == typeof(ApiPulseMarker)))
        {
            return services;
        }

        services.AddSingleton<ApiPulseMarker>();
        services.AddOptions<ApiPulseOptions>();

        // Configuration layers, applied in registration order: code (above), then configuration, then APIPULSE_* variables.
        services.AddSingleton<IConfigureOptions<ApiPulseOptions>, ConfigurationOptionsSetup>();
        services.AddSingleton<IOptionsChangeTokenSource<ApiPulseOptions>, ConfigurationChangeTokenSource>();
        services.TryAddSingleton<IEnvironmentReader, EnvironmentReader>();
        services.AddSingleton<IConfigureOptions<ApiPulseOptions>, EnvironmentOptionsSetup>();

        services.TryAddSingleton<ApiPulseRuntime>();
        services.TryAddSingleton<SpanCollector>();
        services.TryAddSingleton<ApiPulseMetrics>();
        services.TryAddSingleton<EventStore>();
        services.TryAddSingleton<ApiPulseExporter>();
        services.TryAddSingleton<IApiPulseExporter>(sp => sp.GetRequiredService<ApiPulseExporter>());
        services.TryAddSingleton<IApiPulseContext, ApiPulseContext>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IApiPulseSink, HttpApiPulseSink>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IApiPulseSink, LoggingApiPulseSink>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ApiPulseLoggerProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, ApiPulseStartupFilter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ApiPulseStartupService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ApiPulseExporter>()); // guarded by the marker above

        services.AddHttpClient(ApiPulseDefaults.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan) // timeouts are enforced per attempt by the sink
            .RemoveAllLoggers();

        return services;
    }

    /// <summary>Adds API monitoring to a <c>WebApplicationBuilder</c> or <c>HostApplicationBuilder</c>. Equivalent to <c>builder.Services.AddApiPulse(configure)</c>.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Optional code configuration.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddApiPulse<TBuilder>(this TBuilder builder, Action<ApiPulseOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddApiPulse(configure);
        return builder;
    }

    /// <summary>
    /// Registers an additional destination for events, for example to forward them to a queue or a file. All enabled sinks
    /// receive every batch.
    /// </summary>
    /// <typeparam name="TSink">The sink implementation. It is created once and must be thread-safe.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddApiPulseSink<TSink>(this IServiceCollection services)
        where TSink : class, IApiPulseSink
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiPulseSink, TSink>();
        return services;
    }

    private sealed class ApiPulseMarker
    {
    }
}

/// <summary>Pipeline registration for ApiPulse.</summary>
public static class ApiPulseApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the ApiPulse middleware at the current position. Only needed when
    /// <see cref="ApiPulseOptions.AutoRegisterMiddleware"/> is <see langword="false"/>; otherwise the middleware is already
    /// registered at the top of the pipeline and a second registration is a harmless no-op.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException"><c>AddApiPulse</c> was not called.</exception>
    public static IApplicationBuilder UseApiPulse(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService<IApiPulseExporter>() is null)
        {
            throw new InvalidOperationException("ApiPulse services are not registered. Call builder.Services.AddApiPulse() before building the application.");
        }

        return app.UseMiddleware<ApiPulseMiddleware>();
    }
}

/// <summary>Endpoint-level controls for minimal APIs and route groups.</summary>
public static class ApiPulseEndpointExtensions
{
    /// <summary>Excludes this endpoint, or every endpoint of the group, from monitoring. The equivalent of <see cref="ExcludeFromApiPulseAttribute"/>.</summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint or group builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder ExcludeFromApiPulse<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new ExcludeFromApiPulseAttribute());
    }
}

/// <summary>Per-request access to ApiPulse from an <see cref="HttpContext"/>.</summary>
public static class ApiPulseHttpContextExtensions
{
    /// <summary>
    /// Returns the ApiPulse context of this request, for attaching a consumer, tags or errors. When the request is not monitored
    /// (excluded, disabled or outside the pipeline) the returned object is an inert no-op.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A context bound to this request.</returns>
    public static IApiPulseContext GetApiPulse(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ApiPulseContext(context.Items.TryGetValue(ApiPulseMiddleware.ItemKey, out var value) ? value as RequestCapture : null);
    }

    /// <summary>Shortcut for <c>context.GetApiPulse().SetConsumer(id, name, group)</c>.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="id">Stable identifier of the consumer.</param>
    /// <param name="name">Optional display name.</param>
    /// <param name="group">Optional group, tenant or plan.</param>
    public static void SetApiPulseConsumer(this HttpContext context, string id, string? name = null, string? group = null) =>
        context.GetApiPulse().SetConsumer(id, name, group);

    /// <summary>Shortcut for <c>context.GetApiPulse().SetTag(key, value)</c>.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="key">Tag name.</param>
    /// <param name="value">Tag value.</param>
    public static void SetApiPulseTag(this HttpContext context, string key, string? value) =>
        context.GetApiPulse().SetTag(key, value);
}
