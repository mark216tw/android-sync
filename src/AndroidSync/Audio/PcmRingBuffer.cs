namespace Com.AndroidSync.App.Audio;

public sealed class PcmRingBuffer
{
    private readonly Lock _lock = new();
    private readonly byte[] _buffer;
    private readonly int _frameAlign;
    private readonly int _maxBufferedBytes;
    private int _readPos;
    private int _writePos;
    private int _count;

    public PcmRingBuffer(int capacityBytes, int frameAlign = 4, int? maxBufferedBytes = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameAlign);

        _frameAlign = frameAlign;
        var alignedCapacity = Math.Max(frameAlign, (capacityBytes / frameAlign) * frameAlign);
        _buffer = new byte[alignedCapacity];
        var requestedMax = maxBufferedBytes ?? alignedCapacity;
        _maxBufferedBytes = Math.Clamp((requestedMax / frameAlign) * frameAlign, frameAlign, alignedCapacity);
    }

    public int Capacity => _buffer.Length;

    public int BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            var src = data;
            if (src.Length > _maxBufferedBytes)
            {
                var drop = src.Length - _maxBufferedBytes;
                drop -= drop % _frameAlign;
                src = src[drop..];
                if (src.Length > _buffer.Length)
                {
                    src = src[(src.Length - _buffer.Length)..];
                }
            }

            var overflow = (_count + src.Length) - _maxBufferedBytes;
            if (overflow > 0)
            {
                var advance = Math.Min(_count, AlignUp(overflow, _frameAlign));
                _readPos = (_readPos + advance) % _buffer.Length;
                _count -= advance;
            }

            var firstChunk = Math.Min(src.Length, _buffer.Length - _writePos);
            src[..firstChunk].CopyTo(_buffer.AsSpan(_writePos, firstChunk));
            var remaining = src.Length - firstChunk;
            if (remaining > 0)
            {
                src[firstChunk..].CopyTo(_buffer.AsSpan(0, remaining));
            }

            _writePos = (_writePos + src.Length) % _buffer.Length;
            _count += src.Length;
        }
    }

    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_lock)
        {
            if (_count == 0)
            {
                return 0;
            }

            var toRead = Math.Min(destination.Length, _count);
            toRead -= toRead % _frameAlign;
            if (toRead <= 0)
            {
                return 0;
            }

            var firstChunk = Math.Min(toRead, _buffer.Length - _readPos);
            _buffer.AsSpan(_readPos, firstChunk).CopyTo(destination[..firstChunk]);
            var remaining = toRead - firstChunk;
            if (remaining > 0)
            {
                _buffer.AsSpan(0, remaining).CopyTo(destination.Slice(firstChunk, remaining));
            }

            _readPos = (_readPos + toRead) % _buffer.Length;
            _count -= toRead;
            return toRead;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _readPos = 0;
            _writePos = 0;
            _count = 0;
        }
    }

    private static int AlignUp(int value, int align)
    {
        var rem = value % align;
        return rem == 0 ? value : value + (align - rem);
    }
}
