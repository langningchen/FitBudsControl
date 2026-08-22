namespace FitBudsControl.Bluetooth;

public sealed class StreamParser
{
    private readonly List<byte> _buffer = new();

    public event Action<byte[]>? FrameObserved;

    public int BadChecksumCount { get; private set; }
    public int DiscardedBytes { get; private set; }

    public IReadOnlyList<Packet> Feed(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            _buffer.Add(data[i]);
        }

        var output = new List<Packet>();

        while (true)
        {
            if (_buffer.Count < 2)
            {
                break;
            }

            if (_buffer[0] != FitBudsFrameCodec.ResponseHeader0 || _buffer[1] != FitBudsFrameCodec.ResponseHeader1)
            {
                var index = FindHeader(start: 1);
                if (index < 0)
                {
                    var keepLast = _buffer[^1] == FitBudsFrameCodec.ResponseHeader0;
                    var drop = _buffer.Count - (keepLast ? 1 : 0);
                    if (drop > 0)
                    {
                        _buffer.RemoveRange(0, drop);
                        DiscardedBytes += drop;
                    }
                    break;
                }

                _buffer.RemoveRange(0, index);
                DiscardedBytes += index;
            }

            if (_buffer.Count < 5)
            {
                break;
            }

            var frameLength = 6 + _buffer[4];
            if (_buffer.Count < frameLength)
            {
                break;
            }

            var raw = _buffer.GetRange(0, frameLength).ToArray();
            _buffer.RemoveRange(0, frameLength);

            try
            {
                FrameObserved?.Invoke(raw);
            }
            catch
            {
                // Diagnostics must never interrupt stream parsing.
            }

            try
            {
                var packet = FitBudsFrameCodec.ParseResponse(raw);
                if (!packet.ChecksumOk)
                {
                    BadChecksumCount++;
                    continue;
                }
                output.Add(packet);
            }
            catch (InvalidDataException)
            {
                // Resynchronization continues at the next candidate frame.
            }
        }

        return output;
    }

    private int FindHeader(int start)
    {
        for (var i = start; i < _buffer.Count - 1; i++)
        {
            if (_buffer[i] == FitBudsFrameCodec.ResponseHeader0 &&
                _buffer[i + 1] == FitBudsFrameCodec.ResponseHeader1)
            {
                return i;
            }
        }
        return -1;
    }
}
