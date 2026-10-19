namespace SmartX.Core.Devices;

/// <summary>Validation and formatting helpers for device MAC addresses (the unique device identifier).</summary>
public static class MacAddress
{
    /// <summary>
    /// Accepts "24:0a:c4:12:34:56", "24-0A-C4-12-34-56" or "240AC4123456" and returns the
    /// canonical upper-case, colon-separated form.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string hex = input.Trim()
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);

        if (hex.Length != 12)
        {
            return false;
        }

        foreach (char c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        hex = hex.ToUpperInvariant();
        var pairs = new string[6];
        for (int i = 0; i < 6; i++)
        {
            pairs[i] = hex.Substring(i * 2, 2);
        }

        normalized = string.Join(':', pairs);
        return true;
    }

    public static bool IsValid(string? input) => TryNormalize(input, out _);

    /// <summary>URL-friendly form without separators, e.g. "240ac4123456".</summary>
    public static string ToSlug(string mac) =>
        (mac ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>Random, locally administered unicast address (useful when simulating a new device).</summary>
    public static string CreateRandom(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var bytes = new byte[6];
        random.NextBytes(bytes);
        bytes[0] = (byte)((bytes[0] | 0x02) & 0xFE);
        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }
}
