using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SmartX.Core.Devices;

/// <summary>
/// A MAC address stored as its 48-bit numeric value. Used as the key of every device
/// <see cref="Dictionary{TKey,TValue}"/> so lookups never hash a string.
/// <para>
/// Two different MAC addresses always map to two different <see cref="Value"/>s, so equality is exact.
/// <see cref="GetHashCode"/> folds the 48 bits through a 64-bit mixing function (the SplitMix64
/// finaliser) so that sequential addresses from the same vendor prefix still spread evenly
/// across the hash table's buckets instead of clustering.
/// </para>
/// </summary>
public readonly struct MacKey : IEquatable<MacKey>, IComparable<MacKey>
{
    private const ulong Mask48 = 0xFFFF_FFFF_FFFFUL;

    public MacKey(ulong value)
    {
        Value = value & Mask48;
    }

    /// <summary>The address as a 48-bit unsigned number.</summary>
    public ulong Value { get; }

    public static bool TryParse(string? text, out MacKey key)
    {
        key = default;
        if (!MacAddress.TryNormalize(text, out string normalized))
        {
            return false;
        }

        string hex = normalized.Replace(":", string.Empty, StringComparison.Ordinal);
        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
        {
            return false;
        }

        key = new MacKey(value);
        return true;
    }

    public static MacKey Parse(string text) =>
        TryParse(text, out MacKey key) ? key : throw new FormatException($"'{text}' is not a valid MAC address.");

    public bool Equals(MacKey other) => Value == other.Value;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is MacKey other && Equals(other);

    public override int GetHashCode()
    {
        // SplitMix64 finaliser: every input bit affects every output bit.
        ulong z = Value + 0x9E37_79B9_7F4A_7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
        z ^= z >> 31;
        return (int)(z ^ (z >> 32));
    }

    public int CompareTo(MacKey other) => Value.CompareTo(other.Value);

    public static bool operator ==(MacKey left, MacKey right) => left.Equals(right);

    public static bool operator !=(MacKey left, MacKey right) => !left.Equals(right);

    /// <summary>Canonical "24:0A:C4:12:34:56" form.</summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[17];
        int pos = 0;
        for (int i = 5; i >= 0; i--)
        {
            byte b = (byte)(Value >> (i * 8));
            buffer[pos++] = HexDigit(b >> 4);
            buffer[pos++] = HexDigit(b & 0xF);
            if (i > 0)
            {
                buffer[pos++] = ':';
            }
        }

        return new string(buffer);
    }

    private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
}
