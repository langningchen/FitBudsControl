namespace FitBudsControl.Bluetooth;

public sealed record Packet(
    byte[] Raw,
    ushort Category,
    byte[] Payload,
    bool ChecksumOk,
    DateTimeOffset Timestamp)
{
    public string CategoryHex => Category.ToString("X4");
    public string PayloadHex => Convert.ToHexString(Payload);
    public string RawHex => Convert.ToHexString(Raw);
}
