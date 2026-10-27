using Microsoft.AspNetCore.Http;

namespace ApiPulse;

/// <summary>
/// Settings of the built-in dashboard: a local, Telescope-style web UI that lists recent requests, exceptions, logs and
/// outgoing calls of the running application. Bound from <c>ApiPulse:Dashboard</c>.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard is <b>available in the Development environment only</b>, with no setup. Anywhere else it stays off until you
/// provide <see cref="Authorize"/>, because it shows request data (masked, but still sensitive) to whoever can open it.
/// </para>
/// <para>
/// Entries live in a bounded in-memory ring buffer: they are lost on restart and never leave the process. In Development, request
/// and response bodies are shown too (masked) unless <see cref="ApiPulseOptions.CaptureRequestBody"/> /
/// <see cref="ApiPulseOptions.CaptureResponseBody"/> are explicitly set to <see langword="false"/>. Elsewhere they appear only if
/// you turn those options on.
/// </para>
/// </remarks>
public sealed class DashboardOptions
{
    /// <summary>Turns the dashboard on or off. Default <see langword="true"/> (which still means Development-only, see the type remarks).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>URL path the dashboard is served from. Default <c>/apipulse</c>. It is excluded from monitoring automatically.</summary>
    public string Path { get; set; } = "/apipulse";

    /// <summary>Number of most recent requests kept in memory. Default 1000, allowed range 10 to 100 000.</summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>
    /// Decides who may open the dashboard. When set, it replaces the Development-only rule, so this is also how you enable the
    /// dashboard in other environments. It runs after the default authentication scheme (if any) has populated
    /// <c>HttpContext.User</c>. Example: <c>ctx =&gt; ctx.User.IsInRole("Admin")</c>.
    /// </summary>
    public Func<HttpContext, bool>? Authorize { get; set; }
}
