using System.Diagnostics;
using System.Globalization;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;

namespace SmartX.Core.Benchmarks;

/// <summary>
/// Evidence for the Dictionary requirement: compares O(1) hash lookups keyed by <see cref="MacKey"/> with a
/// linear scan of a list (the "nested loop" approach), and counts hash-code collisions of the MacKey hash
/// against a naive hash that keeps only the low 32 bits of the address.
/// </summary>
public static class LookupBenchmark
{
    // A handful of real ESP32 / IoT vendor prefixes (OUIs); devices from different vendors share NIC suffixes.
    private static readonly ulong[] VendorPrefixes =
    {
        0x240AC4, 0x30AEA4, 0x7CDFA1, 0xA4CF12, 0xB4E62D, 0xC45BBE, 0xE89F6D, 0xFCF5C4
    };

    public static LookupBenchmarkDto Run(int devices, int lookups)
    {
        devices = Math.Clamp(devices, 100, 200_000);
        lookups = Math.Clamp(lookups, 100, 100_000);

        var keys = new MacKey[devices];
        for (int i = 0; i < devices; i++)
        {
            ulong prefix = VendorPrefixes[i % VendorPrefixes.Length];
            ulong suffix = (ulong)(i / VendorPrefixes.Length);
            keys[i] = new MacKey((prefix << 24) | suffix);
        }

        var dictionary = new Dictionary<MacKey, int>(devices);
        var list = new List<(MacKey Key, int Index)>(devices);
        for (int i = 0; i < devices; i++)
        {
            dictionary[keys[i]] = i;
            list.Add((keys[i], i));
        }

        var random = new Random(7);
        var probes = new MacKey[lookups];
        for (int i = 0; i < lookups; i++)
        {
            probes[i] = keys[random.Next(devices)];
        }

        long found = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (MacKey probe in probes)
        {
            if (dictionary.TryGetValue(probe, out int index))
            {
                found += index;
            }
        }

        double dictionaryMs = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        foreach (MacKey probe in probes)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Key == probe)
                {
                    found -= list[i].Index;
                    break;
                }
            }
        }

        double listMs = stopwatch.Elapsed.TotalMilliseconds;

        var macHashes = new HashSet<int>();
        var naiveHashes = new HashSet<int>();
        foreach (MacKey key in keys)
        {
            macHashes.Add(key.GetHashCode());
            naiveHashes.Add(unchecked((int)key.Value));
        }

        int macCollisions = devices - macHashes.Count;
        int naiveCollisions = devices - naiveHashes.Count;
        double speedup = dictionaryMs > 0 ? listMs / dictionaryMs : 0;

        string summary = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} lookups over {1:N0} devices: Dictionary {2:0.###} ms vs list scan {3:0.###} ms ({4:0}x faster). " +
            "MacKey hash collisions: {5:N0}; a naive low-32-bit hash would collide {6:N0} times (checksum {7}).",
            lookups,
            devices,
            dictionaryMs,
            listMs,
            speedup,
            macCollisions,
            naiveCollisions,
            found);

        return new LookupBenchmarkDto(
            devices,
            lookups,
            Math.Round(dictionaryMs, 4),
            Math.Round(listMs, 4),
            Math.Round(speedup, 1),
            macHashes.Count,
            macCollisions,
            naiveCollisions,
            summary);
    }
}
