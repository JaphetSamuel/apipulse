using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace ApiPulse;

/// <summary>
/// A destination for batches of captured events. Register your own with
/// <see cref="ApiPulseServiceCollectionExtensions.AddApiPulseSink{TSink}"/> to forward events anywhere (a queue, a file, another
/// vendor). The built-in HTTP sink is used when <see cref="ApiPulseOptions.Endpoint"/> is configured.
/// </summary>
/// <remarks>Implementations must be thread-safe. Exceptions thrown from <see cref="SendAsync"/> are caught, counted and logged by the SDK.</remarks>
public interface IApiPulseSink
{
    /// <summary>Whether this sink currently wants events. When no sink is enabled the SDK skips event capture entirely.</summary>
    bool IsEnabled { get; }

    /// <summary>Delivers a batch. Called from a background worker, never from a request thread.</summary>
    /// <param name="batch">The events to deliver.</param>
    /// <param name="cancellationToken">Cancelled when the export timeout elapses or the host is stopping.</param>
    Task SendAsync(EventBatch batch, CancellationToken cancellationToken);
}

/// <summary>Counters describing the health of the export pipeline.</summary>
/// <param name="Enqueued">Events accepted into the queue.</param>
/// <param name="Exported">Events delivered to a sink successfully.</param>
/// <param name="Dropped">Events discarded because the queue was full.</param>
/// <param name="Failed">Events lost because delivery failed after all retries.</param>
/// <param name="Queued">Events currently waiting for export.</param>
public sealed record ApiPulseStatistics(long Enqueued, long Exported, long Dropped, long Failed, int Queued);

/// <summary>Controls the background export of events.</summary>
public interface IApiPulseExporter
{
    /// <summary>
    /// Sends everything currently queued and waits for it to complete. The SDK flushes automatically on a timer and on shutdown;
    /// call this explicitly in short-lived processes (serverless, tests) or before a deliberate <c>Environment.Exit</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a snapshot of the pipeline counters.</summary>
    ApiPulseStatistics GetStatistics();
}

/// <summary>
/// Lets application code enrich the request that is currently being processed. Inject it anywhere (controllers, minimal API
/// handlers, services, middleware). All members are safe no-ops when called outside a monitored request.
/// </summary>
public interface IApiPulseContext
{
    /// <summary>Whether a request is currently being monitored in this execution context.</summary>
    bool IsActive { get; }

    /// <summary>The W3C trace id of the current request, or <see langword="null"/> outside a monitored request.</summary>
    string? TraceId { get; }

    /// <summary>Declares who is calling the API. Overrides <see cref="ApiPulseOptions.ConsumerResolver"/> and automatic detection.</summary>
    /// <param name="id">Stable identifier of the consumer.</param>
    /// <param name="name">Optional display name.</param>
    /// <param name="group">Optional group, tenant or plan.</param>
    void SetConsumer(string id, string? name = null, string? group = null);

    /// <summary>Declares who is calling the API. Pass <see langword="null"/> to clear.</summary>
    /// <param name="consumer">The consumer.</param>
    void SetConsumer(ApiConsumer? consumer);

    /// <summary>
    /// Attaches a custom key/value to the request (tenant, feature flag, order id...). At most 20 tags are kept; keys are cut at
    /// 64 characters and values at 256. Values of keys matching <see cref="ApiPulseOptions.MaskBodyFields"/> are masked.
    /// A <see langword="null"/> value removes the tag.
    /// </summary>
    /// <param name="key">Tag name.</param>
    /// <param name="value">Tag value.</param>
    void SetTag(string key, string? value);

    /// <summary>Records an exception that was handled by the application so it still appears in the request's errors.</summary>
    /// <param name="exception">The exception.</param>
    void RecordException(Exception exception);

    /// <summary>Guarantees an event is emitted for this request even if it was not sampled (metadata and warnings only).</summary>
    void Keep();

    /// <summary>Discards the event of this request. Metrics are unaffected.</summary>
    void Suppress();
}

/// <summary>
/// Marks a controller, action or endpoint that must not be monitored. Works as an attribute on MVC controllers and actions;
/// for minimal APIs use <see cref="ApiPulseEndpointExtensions.ExcludeFromApiPulse{TBuilder}(TBuilder)"/>.
/// </summary>
/// <remarks>
/// The endpoint is only known once routing has run, so exclusion by attribute drops the event when the request finishes. Prefer
/// <see cref="ApiPulseOptions.ExcludedPaths"/> for endpoints that are called very often.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ExcludeFromApiPulseAttribute : Attribute
{
}

/// <summary>
/// The <see cref="System.Diagnostics.ActivitySource"/> owned by the SDK. Spans started from it while handling a request appear in
/// that request's trace without any further configuration.
/// </summary>
public static class ApiPulseActivitySource
{
    /// <summary>The shared source. Use <c>Instance.StartActivity("my-operation")</c> to record a custom span.</summary>
    public static ActivitySource Instance { get; } = new(ApiPulseDefaults.ActivitySourceName, typeof(ApiPulseActivitySource).Assembly.GetName().Version?.ToString());

    internal static Activity? StartRequestActivity(HttpContext context)
    {
        var headers = context.Request.Headers;
        string? traceParent = headers.TryGetValue("traceparent", out var tp) ? tp.ToString() : null;
        string? traceState = headers.TryGetValue("tracestate", out var ts) ? ts.ToString() : null;
        if (!string.IsNullOrEmpty(traceParent)
            && ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parent))
        {
            return Instance.StartActivity("ApiPulse.Request", ActivityKind.Server, parent);
        }

        return Instance.StartActivity("ApiPulse.Request", ActivityKind.Server);
    }
}
