namespace SmartX.Core.Devices;

public enum SensorCategory : byte
{
    Environmental = 0,
    PowerConsumption = 1,
    Actuator = 2
}

public enum DeviceStatus : byte
{
    Online = 0,
    Stale = 1,
    Offline = 2
}

public enum AttachmentKind : byte
{
    Config = 0,
    Photo = 1,
    Log = 2
}

/// <summary>Validation and normalisation of IEEE 802 MAC addresses (the device primary key).</summary>
public static class MacAddress
{
    /// <summary>Espressif OUIs used by the simulator when generating ESP32 MACs.</summary>
    public static readonly string[] EspressifOuis = ["24:6F:28", "A4:CF:12", "30:AE:A4", "7C:9E:BD", "EC:62:60"];

    /// <summary>Accepts AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF or AABBCCDDEEFF; returns AA:BB:CC:DD:EE:FF.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        Span<char> hex = stackalloc char[12];
        int n = 0;
        foreach (char c in input.Trim())
        {
            if (c is ':' or '-' or '.') continue;
            if (!char.IsAsciiHexDigit(c) || n == 12) return false;
            hex[n++] = char.ToUpperInvariant(c);
        }
        if (n != 12) return false;

        // Reject the all-zero and broadcast addresses - they can never identify a device.
        if (hex.SequenceEqual("000000000000") || hex.SequenceEqual("FFFFFFFFFFFF")) return false;

        normalized = string.Create(17, hex.ToArray(), static (dst, src) =>
        {
            for (int i = 0, j = 0; i < 6; i++)
            {
                if (i > 0) dst[j++] = ':';
                dst[j++] = src[i * 2];
                dst[j++] = src[i * 2 + 1];
            }
        });
        return true;
    }

    public static bool IsValid(string? input) => TryNormalize(input, out _);

    public static string Generate(Random rng)
    {
        var oui = EspressifOuis[rng.Next(EspressifOuis.Length)];
        return $"{oui}:{rng.Next(256):X2}:{rng.Next(256):X2}:{rng.Next(256):X2}";
    }
}
