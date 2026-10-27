using ApiPulse;
using Microsoft.AspNetCore.Mvc;

namespace ApiPulse.Sample;

/// <summary>Controllers need no extra setup: the SDK sees every request that reaches the pipeline.</summary>
[ApiController]
[Route("api/v1/books")]
public sealed class BooksController(ILogger<BooksController> logger) : ControllerBase
{
    [HttpGet]
    public IActionResult List([FromQuery] int limit = 20, [FromQuery] decimal? price_max = null)
    {
        logger.LogInformation("Listing up to {Limit} books", limit);
        return Ok(new[] { new { id = 1, title = "The Pragmatic Programmer" }, new { id = 2, title = "Refactoring" } });
    }

    [HttpGet("{id:int}")]
    public IActionResult Get(int id)
    {
        // Attach whatever context helps you later: it appears next to the request in the dashboard.
        HttpContext.SetApiPulseTag("book.id", id.ToString());
        return Ok(new { id, title = "Refactoring" });
    }

    /// <summary>Never monitored, for example because it is polled every second by a load balancer.</summary>
    [HttpGet("ping")]
    [ExcludeFromApiPulse]
    public IActionResult Ping() => Ok("pong");
}
