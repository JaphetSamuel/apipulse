namespace ApiPulse.Internal;

/// <summary>Bounded buffer holding the first bytes of a body plus the total number of bytes that passed through.</summary>
internal sealed class BodyBuffer
{
    private readonly int _limit;
    private byte[] _data = Array.Empty<byte>();
    private int _length;

    public BodyBuffer(int limit) => _limit = Math.Max(0, limit);

    /// <summary>Total number of bytes observed, including those beyond the capture limit.</summary>
    public long Total { get; private set; }

    public bool Truncated => Total > _length;

    public ReadOnlySpan<byte> Captured => _data.AsSpan(0, _length);

    public bool HasData => _length > 0;

    /// <summary>Counts the bytes and, when <paramref name="capture"/> is set, keeps those that fit under the limit.</summary>
    public void Observe(ReadOnlySpan<byte> bytes, bool capture)
    {
        Total += bytes.Length;
        if (!capture || bytes.IsEmpty)
        {
            return;
        }

        var room = _limit - _length;
        if (room <= 0)
        {
            return;
        }

        var take = Math.Min(room, bytes.Length);
        if (_length + take > _data.Length)
        {
            Array.Resize(ref _data, Math.Min(_limit, Math.Max(_length + take, Math.Max(256, _data.Length * 2))));
        }

        bytes[..take].CopyTo(_data.AsSpan(_length));
        _length += take;
    }
}

/// <summary>
/// Wraps the request body so the application reads it normally while the first bytes are copied aside. Nothing is read ahead
/// of time, so streaming and large uploads are unaffected, and the application sees exactly the bytes it would have seen.
/// </summary>
internal sealed class CapturingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly BodyBuffer _buffer;

    public CapturingReadStream(Stream inner, BodyBuffer buffer)
    {
        _inner = inner;
        _buffer = buffer;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        _buffer.Observe(buffer.AsSpan(offset, n), capture: true);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        _buffer.Observe(buffer[..n], capture: true);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        _buffer.Observe(buffer.AsSpan(offset, n), capture: true);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _buffer.Observe(buffer.Span[..n], capture: true);
        return n;
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Wraps the response body, counting every byte and copying the first ones aside. Whether bytes are copied is decided lazily at
/// the first write, when the response content type and encoding are known, so binary and compressed responses cost nothing.
/// </summary>
internal sealed class CapturingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly BodyBuffer _buffer;
    private readonly Func<bool> _shouldCapture;
    private bool _decided;
    private bool _capture;

    public CapturingWriteStream(Stream inner, BodyBuffer buffer, Func<bool> shouldCapture)
    {
        _inner = inner;
        _buffer = buffer;
        _shouldCapture = shouldCapture;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (!_decided)
        {
            _decided = true;
            try
            {
                _capture = _shouldCapture();
            }
            catch
            {
                _capture = false;
            }
        }

        _buffer.Observe(bytes, _capture);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Observe(buffer.AsSpan(offset, count));
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Observe(buffer);
        _inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Observe(buffer.AsSpan(offset, count));
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Observe(buffer.Span);
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
