using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiPulse.Internal;

/// <summary>
/// Serves the dashboard UI and its JSON API under <see cref="DashboardOptions.Path"/>. Registered ahead of the application's own
/// pipeline, so it works without routing and cannot be shadowed by application endpoints.
/// </summary>
internal sealed class DashboardMiddleware
{
    internal const string CsrfHeader = "X-ApiPulse";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lazy<byte[]> Page = new(LoadPage);

    private readonly RequestDelegate _next;
    private readonly ApiPulseRuntime _runtime;
    private readonly EventStore _store;
    private readonly ILogger _logger;

    public DashboardMiddleware(RequestDelegate next, ApiPulseRuntime runtime, EventStore store, ILoggerFactory loggerFactory)
    {
        _next = next;
        _runtime = runtime;
        _store = store;
        _logger = loggerFactory.CreateLogger("ApiPulse.Middleware");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var settings = _runtime.Current;
        if (!settings.Options.Enabled
            || !settings.Options.Dashboard.Enabled
            || !context.Request.Path.StartsWithSegments(settings.DashboardPath, StringComparison.OrdinalIgnoreCase, out var rest))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!await IsAuthorizedAsync(context, settings).ConfigureAwait(false))
        {
            await DenyAsync(context, settings).ConfigureAwait(false);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        if (!rest.HasValue)
        {
            // Relative URLs in the page need the trailing slash.
            context.Response.Redirect($"{context.Request.PathBase}{settings.DashboardPath}/");
            return;
        }

        var path = rest.Value!;
        if (path == "/")
        {
            await ServePageAsync(context).ConfigureAwait(false);
        }
        else if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await HandleApiAsync(context, path[5..].Trim('/').Split('/'), settings).ConfigureAwait(false);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    // ------------------------------------------------------------------ access

    private async Task<bool> IsAuthorizedAsync(HttpContext context, ApiPulseSettings settings)
    {
        var authorize = settings.Options.Dashboard.Authorize;
        if (authorize is null)
        {
            return settings.IsDevelopment;
        }

        try
        {
            // This middleware runs before UseAuthentication, so give the custom rule an authenticated user to look at.
            if (context.User.Identity?.IsAuthenticated != true
                && context.RequestServices.GetService(typeof(IAuthenticationSchemeProvider)) is IAuthenticationSchemeProvider schemes
                && await schemes.GetDefaultAuthenticateSchemeAsync().ConfigureAwait(false) is not null)
            {
                var result = await context.AuthenticateAsync().ConfigureAwait(false);
                if (result.Succeeded && result.Principal is not null)
                {
                    context.User = result.Principal;
                }
            }

            return authorize(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The ApiPulse dashboard authorization callback threw; access was denied.");
            return false;
        }
    }

    private static async Task DenyAsync(HttpContext context, ApiPulseSettings settings)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(settings.Options.Dashboard.Authorize is null
            ? $"The ApiPulse dashboard is only available in the Development environment, and this application is running as '{settings.HostEnvironmentName}'.\n\n" +
              "To open it locally, start the app with ASPNETCORE_ENVIRONMENT=Development (PowerShell: $env:ASPNETCORE_ENVIRONMENT='Development'; or a launchSettings.json profile).\n" +
              "To open it in this environment, set ApiPulseOptions.Dashboard.Authorize in code to decide who may see it."
            : "You are not allowed to open the ApiPulse dashboard.").ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ page

    private static async Task ServePageAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        // Inline script and styles keep the dashboard a single self-contained file; everything else is locked down.
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        await context.Response.Body.WriteAsync(Page.Value, context.RequestAborted).ConfigureAwait(false);
    }

    private static byte[] LoadPage()
    {
        using var stream = typeof(DashboardMiddleware).Assembly.GetManifestResourceStream("ApiPulse.Dashboard.index.html")
            ?? throw new InvalidOperationException("The embedded ApiPulse dashboard page is missing from the assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ------------------------------------------------------------------ api

    private async Task HandleApiAsync(HttpContext context, string[] parts, ApiPulseSettings settings)
    {
        var method = context.Request.Method;
        var query = context.Request.Query;

        if (parts is ["clear"])
        {
            // A custom header cannot be set by a cross-site form post, which blocks CSRF against this state-changing call.
            if (method != HttpMethods.Post || !context.Request.Headers.ContainsKey(CsrfHeader))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            _store.Clear();
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var all = _store.Snapshot();
        object? body = parts switch
        {
            ["info"] => new
            {
                service = settings.Service.Name,
                environment = settings.Service.Environment,
                version = settings.Service.Version,
                capacity = _store.Capacity,
                bodiesCaptured = settings.CaptureRequestBody || settings.CaptureResponseBody,
            },
            ["overview"] => DashboardQueries.Overview(all, int.TryParse(query["window"], out var w) ? w : 60, _store.Capacity),
            ["requests"] => DashboardQueries.Requests(all, query),
            ["requests", var id] => all.LastOrDefault(e => e.Event.Id == id)?.Event,
            ["exceptions"] => DashboardQueries.Exceptions(all),
            ["logs"] => DashboardQueries.Logs(all, query),
            ["spans"] => DashboardQueries.Spans(all, query),
            _ => null,
        };

        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await context.Response.WriteAsJsonAsync(body, Json, context.RequestAborted).ConfigureAwait(false);
    }
}
