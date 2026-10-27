# ApiPulse for ASP.NET Core

Drop-in API monitoring and analytics for ASP.NET Core, in the spirit of Laravel Telescope. One package, one line in
`Program.cs`, and every request is captured with its **metrics, logs, traces and errors**, correlated by request.
**In development you get a local dashboard at `/apipulse` with no setup**; in any environment you can also export events to a
backend of your choice. Works with minimal APIs and controllers on **.NET 8, 9 and 10**. No dependencies beyond the
ASP.NET Core shared framework.

> `ApiPulse` is a working name. Rename the namespace, assembly and package id (`src/ApiPulse/ApiPulse.csproj`) to taste.

## Quick start

```bash
dotnet add package ApiPulse.AspNetCore
```

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiPulse();

var app = builder.Build();
app.MapGet("/orders/{id}", (int id) => Results.Ok(new { id }));
app.Run();
```

Run the app in Development, send a request, and open **`/apipulse`**. That is the whole integration. There is no
`app.UseApiPulse()` to remember: the middleware registers itself as the outermost component of the pipeline.

To also ship events to a backend, add `o.Endpoint` and `o.ApiKey` (or the `APIPULSE_ENDPOINT` and `APIPULSE_API_KEY`
environment variables). See [Exporting events](#exporting-events).

## Local dashboard

A Telescope-style UI served by your own app, reading from an in-memory buffer of the most recent requests. It needs no database,
no extra process and no configuration.

| Page | What it shows |
|---|---|
| **Overview** | traffic over time, failure rate, median, 95th and 99th percentile latency, busiest routes |
| **Requests** | live list with status, consumer, size, duration; filter by status, method, errors, free text |
| **Request details** | a **timeline** that puts spans, logs and exceptions on one time axis, plus headers, bodies, tags and the raw trace id |
| **Exceptions** | grouped by fingerprint, so one bug seen 200 times is one row, with stack trace and a link to the latest request |
| **Logs** | `ILogger` entries across requests, filterable by level and text, each linked to its request |
| **Outgoing calls** | HTTP calls, database calls and custom spans, linked to the request that made them |

```csharp
builder.Services.AddApiPulse(o =>
{
    o.Dashboard.Path = "/telescope";            // default "/apipulse"
    o.Dashboard.MaxEntries = 2000;               // default 1000 most recent requests, in memory only
    o.Dashboard.Authorize = ctx => ctx.User.IsInRole("Admin");   // see below
});
```

* **Development only by default.** The dashboard shows (masked, but still sensitive) request data, so outside the Development
  environment it answers `403` until you provide `Dashboard.Authorize`. Setting it is also how you enable the dashboard in
  staging or production. The callback runs after the default authentication scheme has populated `HttpContext.User`, so role and
  claim checks work. It cannot be switched on from configuration alone, on purpose.
* **Nothing leaves the process.** Entries live in a bounded ring buffer, are lost on restart, and receive the same masking as
  exported events. `Clear entries` empties it.
* **Payloads are shown in Development**, masked like everything else. `CaptureRequestBody` / `CaptureResponseBody` default to
  *automatic*: on while the dashboard is active in Development, off in every other environment. Set either to `false` to
  hide payloads even in Development, or to `true` to capture them elsewhere. An explicit value always wins.
* The dashboard's own traffic is excluded from monitoring automatically, and it works alongside `Endpoint` or custom sinks.
* The page is a single embedded file: no CDN, no build step, works offline. It escapes everything it displays and ships a strict
  Content-Security-Policy. Turn it off with `Dashboard.Enabled = false` or `APIPULSE_DASHBOARD=false`.

## What is captured

For every request, one `RequestEvent` containing:

| Area | Details |
|---|---|
| **Metrics** | method, path, matched **route template** (`/orders/{id}`), status, duration, request/response size, protocol, client IP |
| **Headers / query** | request and response headers, query string, all with sensitive values masked |
| **Bodies** | request and response bodies (on in Development with the dashboard, off elsewhere unless enabled), size-limited, text types only, masked |
| **Logs** | every `ILogger` entry written while the request was in flight, with level, category, structured properties, span id |
| **Traces** | W3C trace/span ids (honours `traceparent`), plus child spans: outgoing `HttpClient` calls, database calls, your own spans |
| **Errors** | unhandled exceptions, exceptions turned into responses by error handlers, explicitly recorded and logged errors, each with a stable fingerprint for grouping |
| **Context** | the consumer (who is calling) and your own tags |

Separately, **request counts and durations are published as OpenTelemetry-compatible metrics** through the `ApiPulse`
`Meter` (`apipulse.requests`, `apipulse.request.duration`, plus export health counters). These cover 100% of requests
regardless of sampling and work even if no event destination is configured.

## Configuration

Settings come from four layers. **Later layers win**, so operators can override a deployed app without a rebuild.

1. Defaults
2. Code: `AddApiPulse(o => ...)`
3. Configuration: the `ApiPulse` section of `appsettings.json`, user secrets, or `ApiPulse__Setting` environment variables
4. Short environment variables: `APIPULSE_*`

```json
{
  "ApiPulse": {
    "Endpoint": "https://ingest.example.com/v1/events",
    "SamplingRate": 0.25,
    "CaptureRequestBody": true,
    "ClientIp": "Anonymized",
    "MinimumLogLevel": "Information",
    "ExcludedPaths": [ "/internal/*" ],
    "MaskBodyFields": [ "nationalId" ],
    "FlushInterval": "00:00:05"
  }
}
```

**Lists are additive when set through configuration**: your `ExcludedPaths` or `MaskHeaders` are added to the built-in
defaults. To drop a default, clear it in code: `o.ExcludedPaths.Clear();`.

Changes to `appsettings.json` are picked up at runtime for everything that is evaluated per request (`Enabled`,
`SamplingRate`, masking, exclusions, capture flags). Queue size, batch size and the flush interval are read at startup.

### Environment variables

| Variable | Setting |
|---|---|
| `APIPULSE_ENABLED` | `Enabled`. `false` is a kill switch that beats code configuration |
| `APIPULSE_API_KEY`, `APIPULSE_ENDPOINT` | `ApiKey`, `Endpoint` |
| `APIPULSE_SERVICE_NAME`, `APIPULSE_SERVICE_VERSION`, `APIPULSE_ENVIRONMENT` | service identity |
| `APIPULSE_SAMPLING_RATE` | `SamplingRate` (0 to 1) |
| `APIPULSE_CAPTURE_REQUEST_BODY`, `APIPULSE_CAPTURE_RESPONSE_BODY` | body capture (`true`/`false`/`1`/`0`) |
| `APIPULSE_CAPTURE_LOGS`, `APIPULSE_MIN_LOG_LEVEL` | log capture |
| `APIPULSE_LOG_EVENTS` | write a one-line summary of each event to the log |
| `APIPULSE_DASHBOARD`, `APIPULSE_DASHBOARD_PATH` | turn the dashboard on or off, change its path |
| `APIPULSE_EXCLUDED_PATHS`, `APIPULSE_MASK_HEADERS`, `APIPULSE_MASK_QUERY_PARAMS`, `APIPULSE_MASK_BODY_FIELDS` | comma-separated, added to the defaults |

Unparseable values are ignored. Out-of-range values are clamped. **The SDK never prevents your application from starting.**

### Settings reference

| Setting | Default | Notes |
|---|---|---|
| `Enabled` | `true` | Master switch, checked per request |
| `Dashboard.Enabled`, `Dashboard.Path`, `Dashboard.MaxEntries`, `Dashboard.Authorize` | `true`, `/apipulse`, 1000, none | Development-only unless `Authorize` is set |
| `Endpoint`, `ApiKey` | none | With no endpoint, custom sink or dashboard, nothing is exported and a startup warning says so |
| `AutoRegisterMiddleware` | `true` | Set `false` and call `app.UseApiPulse()` to choose the position yourself |
| `CaptureRequestHeaders` / `CaptureResponseHeaders` / `CaptureQueryString` | `true` | |
| `CaptureRequestBody` / `CaptureResponseBody` | automatic (`null`) | On in Development while the dashboard is active, off elsewhere. An explicit `true`/`false` always wins. Bodies often contain personal data |
| `MaxBodySize` | 32 KiB | Per body. Larger bodies are truncated and flagged; sizes stay exact |
| `BodyContentTypes` | json, xml, `text/*`, form-urlencoded | Never images, multipart uploads or compressed bodies |
| `ClientIp` | `Full` | `Anonymized` zeroes the host part, `None` records nothing |
| `CaptureLogs`, `MinimumLogLevel` | `true`, `Information` | |
| `MaxLogsPerRequest`, `MaxLogMessageLength` | 100, 4096 | Overflow is counted in `DroppedLogs` |
| `CaptureSpans`, `SpanSources`, `MaxSpansPerRequest` | `true`, see below, 50 | |
| `ExcludedPaths` | health, metrics, swagger/openapi, favicon... | Glob patterns, case-insensitive |
| `ExcludedMethods`, `Filter` | none | e.g. `OPTIONS` |
| `SamplingRate`, `AlwaysCaptureErrors`, `SlowRequestThreshold`, `Sampler` | `1.0`, `true`, none, none | |
| `MaskValue` | `******` | |
| `MaskHeaders`, `MaskQueryParams`, `MaskBodyFields`, `MaskValuePatterns` | sensible lists | See Masking |
| `ConsumerResolver`, `AutoDetectConsumer` | none, `true` | |
| `BeforeSend` | none | Inspect, change or drop any event |
| `MaxQueueSize`, `BatchSize`, `FlushInterval`, `ExportTimeout`, `MaxRetries`, `ShutdownTimeout`, `CompressPayloads` | 10 000, 100, 5 s, 10 s, 3, 5 s, `true` | |

Every property is documented in `ApiPulseOptions` (IntelliSense shows the same text).

## Masking sensitive data

Masking runs **before anything is queued**, so secrets never leave the request.

* **Headers:** `Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, anything matching `*token*`, `*secret*`, `*password*`... (`MaskHeaders`)
* **Query parameters:** `token`, `key`, `code`, `signature`, `*password*`... (`MaskQueryParams`)
* **Bodies, log properties and span tags:** fields named like `*password*`, `*secret*`, `*token*`, `cvv`, `ssn`, `otp`... at any
  depth of JSON, in XML elements and in form bodies (`MaskBodyFields`). The value is replaced whatever its type, including
  objects and arrays. Truncated or malformed JSON is handled safely: anything that could not be parsed is dropped rather than
  risked.
* **Free text** (bodies, log messages, exception messages): regular expressions in `MaskValuePatterns`. Bearer tokens and JWTs are
  masked by default. Add `ApiPulseDefaults.CreditCardPattern` or your own. Patterns run in non-backtracking mode, so a bad
  pattern cannot cause a ReDoS.
* **Everything else:** `BeforeSend` gives you the final event to scrub (for example IDs embedded in `Path`).

Names support `*` wildcards and ignore case. If masking itself fails for a value, the value is replaced entirely, never recorded as-is.

## Excluding paths, methods and endpoints

```csharp
builder.Services.AddApiPulse(o =>
{
    o.ExcludedPaths.Add("/internal/*");           // glob, case-insensitive
    o.ExcludedMethods.Add("OPTIONS");              // CORS preflights
    o.Filter = ctx => !ctx.Request.Headers.ContainsKey("X-Synthetic-Check");
});

app.MapGet("/ping", () => "pong").ExcludeFromApiPulse();   // minimal API (also on route groups)

[ExcludeFromApiPulse]                                       // controller or action
public IActionResult Ping() => Ok();
```

Health checks (`/health`, `/healthz`, `/ready`, `/live`...), `/metrics`, OpenAPI/Swagger UIs and the favicon are excluded out of the box.
Prefer path patterns for very hot endpoints: the attribute is evaluated when the request finishes.

## Sampling

```csharp
o.SamplingRate = 0.1;            // capture 10% of requests in full
o.AlwaysCaptureErrors = true;    // default: failed requests are always reported
o.SlowRequestThreshold = TimeSpan.FromSeconds(2);   // ...and so are slow ones
o.Sampler = ctx => ctx.Request.Path.StartsWithSegments("/payments") ? true : null;  // null = use the rate
```

* The decision is derived from the **trace id**, so every service in a distributed trace makes the same choice.
* **Metrics always cover 100%** of requests, regardless of sampling.
* An unsampled request that fails (5xx or exception) or is slow is still reported, as a lightweight event: metadata,
  exceptions and warning-or-higher logs, but no bodies, headers or spans. Its `Sampled` flag is `false`.

## Consumer and request context

Tell ApiPulse who is calling and attach your own context. Everything is a safe no-op outside a monitored request.

```csharp
// Automatic: the authenticated user (claim "sub", then nameidentifier) becomes the consumer.

// Per application: resolve from anything on the request, once, when the request finishes.
o.ConsumerResolver = ctx => ctx.Request.Headers.TryGetValue("X-Client-Id", out var id)
    ? new ApiConsumer(id!, Group: "partners") : null;

// Explicit, from anywhere (controller, minimal API, service, middleware). Wins over the two above.
app.MapGet("/orders/{id}", (int id, IApiPulseContext pulse) =>
{
    pulse.SetConsumer("acme-42", "ACME Corp", group: "enterprise");
    pulse.SetTag("order.id", id.ToString());          // up to 20 tags; sensitive key names are masked
    pulse.Keep();                                      // report this request even if it was not sampled
    return Results.Ok();
});

// Or from an HttpContext:
httpContext.SetApiPulseConsumer("acme-42");
httpContext.SetApiPulseTag("feature", "beta");
```

Also available: `pulse.RecordException(ex)` for errors you handle but still want to see, and `pulse.Suppress()` to drop one request.

## Logs, correlated to the right request

ApiPulse registers an ordinary `ILogger` provider (alias `ApiPulse`); it coexists with console, file, Serilog or OpenTelemetry
logging. Each entry written while a request is in flight is attached to that request, **including entries from services,
background tasks and parallel work started by the request**, and never to another request, however concurrent.

```csharp
logger.LogInformation("Loading order {OrderId}", id);   // appears on the request, with OrderId as a structured property
```

* Normal `Logging:LogLevel` filters apply to it. To tune it separately, use `"Logging": { "ApiPulse": { "LogLevel": { "Default": "Warning" } } }`.
* Entries written outside a request (startup, hosted services) are not captured, and neither is the SDK's own logging.
* Logs from the host before the pipeline runs (for example Kestrel connection messages) are not part of any request.
* `LogError(exception, ...)` also registers the exception as an error of the request (once, even if seen by several layers).

## Errors

* Exceptions escaping an endpoint are captured and **re-thrown untouched**: the SDK never swallows or wraps them.
* Exceptions turned into responses by `UseExceptionHandler`, `IExceptionHandler` or the developer exception page are captured too,
  and the route is recovered even though those handlers clear it. They are marked `Handled = false` because they escaped your endpoint.
* Each exception carries type, message, stack trace, up to three levels of inner exceptions, and a **fingerprint** (type plus top frames, no line numbers) so identical bugs group together across deployments.
* Client disconnects are reported as `Aborted` with status 499, not as errors.

## Traces

Every event carries the W3C `traceId`/`spanId` (and `parentSpanId` when a `traceparent` header arrived). Child spans created during
the request are attached automatically:

* Outgoing `HttpClient` calls. On .NET 9 and 10 these carry method, status, host and URL; on .NET 8 the runtime emits a legacy activity without standard tags.
* Database and other sources listed in `SpanSources` (default: `System.Net.Http`, `OpenTelemetry.Instrumentation.*`, `Npgsql`, `Microsoft.EntityFrameworkCore`). Wildcards are allowed.
* Your own spans: `using var span = ApiPulseActivitySource.Instance.StartActivity("price-lookup");`

Only standard tag families (`http.*`, `url.*`, `server.*`, `db.system`...) are recorded. Free-form tags such as `db.statement` are
deliberately left out because they often contain user data. ApiPulse only listens; it does not change the sampling flag
that other tracing libraries propagate, so it coexists with OpenTelemetry.

## Exporting events

Events are queued in memory and sent in batches by a background worker, so **requests never wait for the network**.

* Batches are POSTed as JSON (gzip above 1 KiB) to `Endpoint`, with the key in `X-Api-Key`. Schema: `EventBatch` / `RequestEvent` in `src/ApiPulse/Models`.
* Transient failures (network errors, 408, 429, 5xx) are retried with exponential backoff and jitter. Other 4xx responses drop the batch immediately.
* The queue is bounded (`MaxQueueSize`). When it is full new events are dropped and counted. Nothing blocks.
* Pending events are flushed on shutdown (`ShutdownTimeout`). In short-lived processes call `await IApiPulseExporter.FlushAsync()`.
* `IApiPulseExporter.GetStatistics()` reports enqueued, exported, dropped and failed counts. The same counters are exported as metrics.

Send events anywhere else by implementing `IApiPulseSink`:

```csharp
public sealed class QueueSink : IApiPulseSink
{
    public bool IsEnabled => true;
    public Task SendAsync(EventBatch batch, CancellationToken ct) { /* forward it */ return Task.CompletedTask; }
}

builder.Services.AddApiPulse().AddApiPulseSink<QueueSink>();
```

To use a proxy or custom TLS settings for the built-in sink, configure the named client:
`services.AddHttpClient(ApiPulseDefaults.HttpClientName).ConfigurePrimaryHttpMessageHandler(...)`.

## Safety and performance guarantees

* **Never breaks the app.** Every SDK code path is guarded; internal errors are logged at most once a minute and the request proceeds untouched. User callbacks (`Filter`, `Sampler`, `ConsumerResolver`, `BeforeSend`) that throw are contained.
* **Transparent to the application.** Request bodies are tee'd as the application reads them (nothing is read ahead, streaming and large uploads work); response bodies are tee'd as they are written. Only bodies with eligible content types are ever copied, and only up to `MaxBodySize`.
* **Cheap when it has nothing to do.** With no destination configured or the SDK disabled, requests take a fast path. Bodies are only wrapped when body capture is on and the request is sampled.
* **Bounded memory.** Queue, logs, spans, tags, exceptions, headers and bodies all have caps.
* **Compressed bodies are skipped.** If request or response bodies are compressed, they are not recorded (their size still is).

### Things to know

* The middleware sits at the very top, so durations include the whole pipeline. Client IP, scheme and host are read when the request ends, so values rewritten by `UseForwardedHeaders` are what gets recorded.
* Bodies are captured only as far as the application reads or writes them. An unread request body is not recorded.
* Without `AddApiPulse`, nothing runs. Calling `AddApiPulse` twice is harmless.
* `UseApiPulse()` without `AddApiPulse()` throws with an explanatory message.
* Unhandled exceptions outside requests (`AppDomain.UnhandledException`, unobserved task exceptions) are out of scope.
* SDK versions: built for .NET 8, 9 and 10 (.NET 8 and 9 reach end of support on 10 November 2026).

## Repository layout

```
src/ApiPulse/            the SDK (multi-targets net8.0, net9.0, net10.0)
tests/ApiPulse.Tests/    183 tests, run on all three frameworks
samples/ApiPulse.Sample/ minimal APIs + controllers; run it in Development and open /apipulse
```

```bash
dotnet build
dotnet test                     # all three frameworks
dotnet test -f net8.0           # one framework
dotnet pack src/ApiPulse -c Release
dotnet run --project samples/ApiPulse.Sample -f net10.0     # uses the "http" launch profile: Development, http://localhost:5080/apipulse/
```

### The dashboard answers 403

The dashboard is Development-only. A 403 whose message names the environment means your app is running as `Production` (the
default when `ASPNETCORE_ENVIRONMENT` is not set, which is also what `dotnet run` does without a `launchSettings.json`).

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'; dotnet run          # PowerShell
```
```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run                    # bash
```

Visual Studio and Rider set it for you through `Properties/launchSettings.json` (profile variable `ASPNETCORE_ENVIRONMENT`).
To open the dashboard in another environment on purpose, set `Dashboard.Authorize` in code (see [Local dashboard](#local-dashboard)).
