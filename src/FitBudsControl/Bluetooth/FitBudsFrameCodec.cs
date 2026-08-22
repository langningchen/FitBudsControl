namespace FitBudsControl.Bluetooth;

public static class FitBudsFrameCodec
{
    public const byte RequestHeader0 = 0xAA;
    public const byte RequestHeader1 = 0xEC;
    public const byte ResponseHeader0 = 0xBB;
    public const byte ResponseHeader1 = 0xEC;

    public static byte[] BuildPacket(ushort category, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "数据长度超出可用范围");
        }

        var raw = new byte[6 + payload.Length];
        raw[0] = RequestHeader0;
        raw[1] = RequestHeader1;
        raw[2] = (byte)(category >> 8);
        raw[3] = (byte)(category & 0xFF);
        raw[4] = (byte)payload.Length;
        payload.CopyTo(raw.AsSpan(5));
        raw[^1] = Checksum(raw.AsSpan(0, raw.Length - 1));
        return raw;
    }

    public static Packet ParseResponse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 6)
        {
            throw new InvalidDataException("收到的数据不完整");
        }

        if (raw[0] != ResponseHeader0 || raw[1] != ResponseHeader1)
        {
            throw new InvalidDataException("收到的数据无法识别");
        }

        var expectedLength = 6 + raw[4];
        if (raw.Length != expectedLength)
        {
            throw new InvalidDataException("收到的数据长度不正确");
        }

        var bytes = raw.ToArray();
        var checksumOk = Checksum(raw[..^1]) == raw[^1];
        var category = (ushort)((raw[2] << 8) | raw[3]);
        var payload = raw.Slice(5, raw[4]).ToArray();
        return new Packet(bytes, category, payload, checksumOk, DateTimeOffset.Now);
    }

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var value in bytes)
        {
            sum = (sum + value) & 0xFF;
        }
        return (byte)sum;
    }
}
