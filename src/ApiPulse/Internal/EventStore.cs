namespace ApiPulse.Internal;

/// <summary>A stored event with a monotonically increasing sequence number, used as a paging and live-polling cursor.</summary>
internal sealed record StoredEntry(long Seq, RequestEvent Event);

/// <summary>
/// Bounded in-memory ring buffer behind the dashboard. Writes happen on the request path, so <see cref="Add"/> only takes a short
/// lock and does no allocation beyond the entry itself. Readers get a copied snapshot and filter it without holding the lock.
/// </summary>
internal sealed class EventStore
{
    private readonly ApiPulseRuntime _runtime;
    private readonly object _gate = new();
    private readonly StoredEntry?[] _ring;
    private int _next;
    private int _count;
    private long _seq;

    public EventStore(ApiPulseRuntime runtime)
    {
        _runtime = runtime;
        _ring = new StoredEntry?[runtime.Current.DashboardMaxEntries];
    }

    /// <summary>Whether the dashboard currently wants events. Evaluated per request so configuration reloads take effect.</summary>
    public bool IsActive => _runtime.Current.DashboardActive;

    public int Capacity => _ring.Length;

    public void Add(RequestEvent evt)
    {
        lock (_gate)
        {
            _ring[_next] = new StoredEntry(++_seq, evt);
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>Returns the stored entries from oldest to newest.</summary>
    public StoredEntry[] Snapshot()
    {
        lock (_gate)
        {
            var result = new StoredEntry[_count];
            var start = (_next - _count + _ring.Length) % _ring.Length;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _ring[(start + i) % _ring.Length]!;
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _next = 0;
            _count = 0;
        }
    }
}
