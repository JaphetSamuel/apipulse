using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiPulse.Internal;

/// <summary>Read-only queries over a snapshot of the event store, shaped for the dashboard UI.</summary>
internal static class DashboardQueries
{
    private const int BucketCount = 30;

    // ------------------------------------------------------------------ requests

    public static object Requests(StoredEntry[] all, IQueryCollection query)
    {
        var limit = Math.Clamp(Int(query["limit"], 50), 1, 200);
        var before = Long(query["before"], long.MaxValue);
        var after = Long(query["after"], 0);
        var filter = BuildFilter(query);

        var items = new List<object>(limit);
        var total = 0;
        var hasMore = false;
        for (var i = all.Length - 1; i >= 0; i--)
        {
            var entry = all[i];
            if (!filter(entry.Event))
            {
                continue;
            }

            total++;
            if (entry.Seq >= before || entry.Seq <= after)
            {
                continue;
            }

            if (items.Count < limit)
            {
                items.Add(Summary(entry));
            }
            else
            {
                hasMore = true;
            }
        }

        return new { items, total, hasMore, latest = all.Length == 0 ? 0 : all[^1].Seq, stored = all.Length };
    }

    private static object Summary(StoredEntry entry)
    {
        var e = entry.Event;
        return new
        {
            seq = entry.Seq,
            id = e.Id,
            timestamp = e.Timestamp,
            method = e.Method,
            path = e.Path,
            route = e.Route,
            query = e.Query,
            status = e.StatusCode,
            durationMs = e.DurationMs,
            requestSize = e.RequestSize,
            responseSize = e.ResponseSize,
            consumer = e.Consumer?.Name ?? e.Consumer?.Id,
            logs = e.Logs?.Count ?? 0,
            exceptions = e.Exceptions?.Count ?? 0,
            spans = e.Spans?.Count ?? 0,
            sampled = e.Sampled,
            aborted = e.Aborted,
        };
    }

    private static Func<RequestEvent, bool> BuildFilter(IQueryCollection query)
    {
        var status = query["status"].ToString();
        var method = query["method"].ToString();
        var text = query["q"].ToString();
        var consumer = query["consumer"].ToString();
        var route = query["route"].ToString();
        var errorsOnly = query["errors"] == "1";
        var slow = Int(query["slow"], 0);

        return e =>
        {
            if (status.Length > 0 && !StatusMatches(status, e.StatusCode))
            {
                return false;
            }

            if (method.Length > 0 && !string.Equals(e.Method, method, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (errorsOnly && e.StatusCode < 500 && e.Exceptions is not { Count: > 0 })
            {
                return false;
            }

            if (consumer.Length > 0 && e.Consumer?.Id != consumer)
            {
                return false;
            }

            if (route.Length > 0 && e.Route != route)
            {
                return false;
            }

            if (slow > 0 && e.DurationMs < slow)
            {
                return false;
            }

            return text.Length == 0 || Contains(e.Path, text) || Contains(e.Route, text) || Contains(e.Query, text)
                || Contains(e.TraceId, text) || Contains(e.Consumer?.Id, text) || Contains(e.Consumer?.Name, text)
                || Contains(e.ClientIp, text);
        };
    }

    private static bool StatusMatches(string filter, int status) => filter switch
    {
        "2xx" => status is >= 200 and < 300,
        "3xx" => status is >= 300 and < 400,
        "4xx" => status is >= 400 and < 500,
        "5xx" => status >= 500,
        _ => int.TryParse(filter, out var exact) && status == exact,
    };

    // ------------------------------------------------------------------ overview

    public static object Overview(StoredEntry[] all, int windowMinutes, int capacity)
    {
        windowMinutes = Math.Clamp(windowMinutes, 5, 24 * 60);
        var now = DateTimeOffset.UtcNow;
        var from = now.AddMinutes(-windowMinutes);
        var width = TimeSpan.FromMinutes(windowMinutes) / BucketCount;

        var buckets = new (int Count, int Errors, double Sum)[BucketCount];
        var durations = new List<double>();
        int c2 = 0, c3 = 0, c4 = 0, c5 = 0, withExceptions = 0;
        var routes = new Dictionary<(string Method, string Route), List<RequestEvent>>();

        foreach (var entry in all)
        {
            var e = entry.Event;
            if (e.Timestamp < from)
            {
                continue;
            }

            var index = Math.Clamp((int)((e.Timestamp - from) / width), 0, BucketCount - 1);
            var failed = e.StatusCode >= 500 || e.Exceptions is { Count: > 0 };
            buckets[index].Count++;
            buckets[index].Sum += e.DurationMs;
            if (failed)
            {
                buckets[index].Errors++;
            }

            durations.Add(e.DurationMs);
            switch (e.StatusCode / 100)
            {
                case 2: c2++; break;
                case 3: c3++; break;
                case 4: c4++; break;
                case 5: c5++; break;
            }

            if (e.Exceptions is { Count: > 0 })
            {
                withExceptions++;
            }

            var key = (e.Method, e.Route ?? "unmatched");
            if (!routes.TryGetValue(key, out var list))
            {
                routes[key] = list = new List<RequestEvent>();
            }

            list.Add(e);
        }

        durations.Sort();
        var topRoutes = routes
            .OrderByDescending(r => r.Value.Count)
            .Take(10)
            .Select(r =>
            {
                var sorted = r.Value.Select(x => x.DurationMs).Order().ToArray();
                return new
                {
                    method = r.Key.Method,
                    route = r.Key.Route,
                    count = r.Value.Count,
                    avgMs = Math.Round(sorted.Average(), 1),
                    p95Ms = Math.Round(Percentile(sorted, 0.95), 1),
                    clientErrors = r.Value.Count(x => x.StatusCode is >= 400 and < 500),
                    serverErrors = r.Value.Count(x => x.StatusCode >= 500),
                };
            })
            .ToArray();

        var sortedAll = durations.ToArray();
        return new
        {
            windowMinutes,
            total = sortedAll.Length,
            status = new { c2xx = c2, c3xx = c3, c4xx = c4, c5xx = c5 },
            requestsWithExceptions = withExceptions,
            avgMs = sortedAll.Length == 0 ? 0 : Math.Round(sortedAll.Average(), 1),
            p50Ms = Math.Round(Percentile(sortedAll, 0.50), 1),
            p95Ms = Math.Round(Percentile(sortedAll, 0.95), 1),
            p99Ms = Math.Round(Percentile(sortedAll, 0.99), 1),
            buckets = buckets.Select((b, i) => new
            {
                t = from + (width * i),
                count = b.Count,
                errors = b.Errors,
                avgMs = b.Count == 0 ? 0 : Math.Round(b.Sum / b.Count, 1),
            }),
            topRoutes,
            stored = all.Length,
            capacity,
        };
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    // ------------------------------------------------------------------ exceptions

    /// <summary>Groups exceptions by fingerprint so one bug seen a hundred times is one row.</summary>
    public static object Exceptions(StoredEntry[] all)
    {
        var groups = new Dictionary<string, (ExceptionInfo Latest, StoredEntry Entry, int Count, HashSet<string> Routes, DateTimeOffset First, bool Unhandled)>();
        foreach (var entry in all)
        {
            foreach (var ex in entry.Event.Exceptions ?? Enumerable.Empty<ExceptionInfo>())
            {
                var route = $"{entry.Event.Method} {entry.Event.Route ?? entry.Event.Path}";
                if (groups.TryGetValue(ex.Fingerprint, out var g))
                {
                    g.Routes.Add(route);
                    groups[ex.Fingerprint] = (ex, entry, g.Count + 1, g.Routes, g.First, g.Unhandled || !ex.Handled);
                }
                else
                {
                    groups[ex.Fingerprint] = (ex, entry, 1, new HashSet<string> { route }, entry.Event.Timestamp, !ex.Handled);
                }
            }
        }

        return new
        {
            items = groups
                .OrderByDescending(g => g.Value.Entry.Event.Timestamp)
                .Select(g => new
                {
                    fingerprint = g.Key,
                    type = g.Value.Latest.Type,
                    message = g.Value.Latest.Message,
                    stackTrace = g.Value.Latest.StackTrace,
                    count = g.Value.Count,
                    unhandled = g.Value.Unhandled,
                    firstSeen = g.Value.First,
                    lastSeen = g.Value.Entry.Event.Timestamp,
                    routes = g.Value.Routes.Take(5).ToArray(),
                    latestRequestId = g.Value.Entry.Event.Id,
                }).ToArray(),
        };
    }

    // ------------------------------------------------------------------ logs

    public static object Logs(StoredEntry[] all, IQueryCollection query)
    {
        var limit = Math.Clamp(Int(query["limit"], 200), 1, 500);
        var text = query["q"].ToString();
        var min = Enum.TryParse<LogLevel>(query["level"], ignoreCase: true, out var level) ? level : LogLevel.Trace;

        var rows = new List<(DateTimeOffset Time, object Row)>();
        for (var i = all.Length - 1; i >= 0 && rows.Count < limit * 4; i--)
        {
            var e = all[i].Event;
            foreach (var log in e.Logs ?? Enumerable.Empty<LogEntry>())
            {
                if (Enum.TryParse<LogLevel>(log.Level, out var l) && l < min)
                {
                    continue;
                }

                if (text.Length > 0 && !Contains(log.Message, text) && !Contains(log.Category, text))
                {
                    continue;
                }

                rows.Add((log.Timestamp, new
                {
                    time = log.Timestamp,
                    level = log.Level,
                    category = log.Category,
                    message = log.Message,
                    hasException = log.Exception is not null,
                    requestId = e.Id,
                    request = $"{e.Method} {e.Route ?? e.Path}",
                    status = e.StatusCode,
                }));
            }
        }

        return new { items = rows.OrderByDescending(r => r.Time).Take(limit).Select(r => r.Row).ToArray() };
    }

    // ------------------------------------------------------------------ spans

    public static object Spans(StoredEntry[] all, IQueryCollection query)
    {
        var limit = Math.Clamp(Int(query["limit"], 200), 1, 500);
        var text = query["q"].ToString();
        var rows = new List<(DateTimeOffset Time, object Row)>();
        for (var i = all.Length - 1; i >= 0 && rows.Count < limit * 4; i--)
        {
            var e = all[i].Event;
            foreach (var span in e.Spans ?? Enumerable.Empty<SpanInfo>())
            {
                var target = span.Tags is null ? null : string.Join(' ', span.Tags.Where(t => t.Key is "url.full" or "http.url" or "db.system" or "server.address").Select(t => t.Value));
                if (text.Length > 0 && !Contains(span.Name, text) && !Contains(target, text) && !Contains(span.Source, text))
                {
                    continue;
                }

                var time = e.Timestamp.AddMilliseconds(span.StartOffsetMs);
                rows.Add((time, new
                {
                    time,
                    name = span.Name,
                    source = span.Source,
                    kind = span.Kind,
                    status = span.Status,
                    durationMs = Math.Round(span.DurationMs, 1),
                    tags = span.Tags,
                    requestId = e.Id,
                    request = $"{e.Method} {e.Route ?? e.Path}",
                }));
            }
        }

        return new { items = rows.OrderByDescending(r => r.Time).Take(limit).Select(r => r.Row).ToArray() };
    }

    // ------------------------------------------------------------------ helpers

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static int Int(string? value, int fallback) => int.TryParse(value, out var v) ? v : fallback;

    private static long Long(string? value, long fallback) => long.TryParse(value, out var v) ? v : fallback;
}
