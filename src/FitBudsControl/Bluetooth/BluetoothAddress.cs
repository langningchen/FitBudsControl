using System.Globalization;

namespace FitBudsControl.Bluetooth;

public static class BluetoothAddress
{
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Replace(":", string.Empty).Replace("-", string.Empty).Trim();
        if (normalized.Length != 12)
        {
            return false;
        }

        return ulong.TryParse(normalized, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    public static ulong Parse(string text)
        => TryParse(text, out var result)
            ? result
            : throw new FormatException("耳机地址格式不正确");

    public static string Format(ulong address)
    {
        var hex = address.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
