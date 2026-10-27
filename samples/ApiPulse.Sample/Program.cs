using ApiPulse;

var builder = WebApplication.CreateBuilder(args);

// The whole integration. In Development this also turns on the local dashboard at /apipulse.
// Defaults, appsettings.json ("ApiPulse" section) and APIPULSE_* environment variables are applied automatically;
// the callback is only needed for things that cannot be expressed as plain settings.
builder.Services.AddApiPulse(options =>
{
    options.ConsumerResolver = ctx =>
        ctx.Request.Headers.TryGetValue("X-Client-Id", out var id) && !string.IsNullOrWhiteSpace(id)
            ? new ApiConsumer(id.ToString(), Group: ctx.Request.Headers["X-Plan"].ToString())
            : null;
});

builder.Services.AddControllers();
builder.Services.AddResponseCompression();
builder.Services.AddHttpClient();

var app = builder.Build();
app.UseResponseCompression();

// ---------------------------------------------------------------- minimal API endpoints

var orders = app.MapGroup("/api/v1/orders");

orders.MapGet("/{id:int}", async (int id, ILogger<Program> logger, IApiPulseContext pulse) =>
{
    logger.LogInformation("Loading order {OrderId}", id);
    pulse.SetTag("order.id", id.ToString());
    await Task.Delay(Random.Shared.Next(5, 60));
    return id == 404 ? Results.NotFound() : Results.Ok(new { id, status = "shipped", total = 42.5m });
});

orders.MapPost("/", (NewOrder order, ILogger<Program> logger) =>
{
    if (order.Quantity <= 0)
    {
        logger.LogWarning("Rejected order for {Isbn} with quantity {Quantity}", order.Isbn, order.Quantity);
        return Results.BadRequest(new { error = "quantity must be positive" });
    }

    logger.LogInformation("Created order for {Isbn}", order.Isbn);
    return Results.Created("/api/v1/orders/1", new { id = 1, order.Isbn, order.Quantity, apiToken = "never-shown-in-monitoring" });
});

app.MapGet("/api/v1/crash", (ILogger<Program> logger) =>
{
    logger.LogInformation("About to fail");
    throw new InvalidOperationException("Demo failure: this is what an unhandled error looks like in ApiPulse.");
});

// Outgoing HTTP calls and custom spans made while handling a request show up on that request's timeline.
app.MapGet("/api/v1/outgoing", async (IHttpClientFactory factory, HttpContext context, ILogger<Program> logger) =>
{
    logger.LogInformation("Looking up prices");
    using (var span = ApiPulseActivitySource.Instance.StartActivity("price-lookup"))
    {
        span?.SetTag("http.request.method", "GET");
        await Task.Delay(25);
    }

    var upstream = await factory.CreateClient().GetStringAsync($"{context.Request.Scheme}://{context.Request.Host}/api/v1/orders/1?token=abc");
    logger.LogInformation("Prices received");
    return Results.Ok(new { upstream });
});

app.MapGet("/health", () => Results.Ok("healthy")); // excluded by default

app.MapControllers();
app.Run();

public sealed record NewOrder(string Isbn, int Quantity);

public partial class Program;
