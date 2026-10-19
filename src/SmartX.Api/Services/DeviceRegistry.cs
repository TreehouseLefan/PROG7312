using System.Diagnostics.CodeAnalysis;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Live device registry.
/// <list type="bullet">
/// <item>Primary store: <c>Dictionary&lt;MacKey, SensorDevice&gt;</c>, so every incoming packet finds its device in O(1)
/// with no string hashing (see <see cref="MacKey"/>).</item>
/// <item>Secondary indexes: <c>Dictionary&lt;SensorCategory, HashSet&lt;MacKey&gt;&gt;</c>, one per Sub-Zone, and an
/// inverted index <c>Dictionary&lt;string token, HashSet&lt;MacKey&gt;&gt;</c> over device names, so search and
/// filter results come from set intersections instead of scanning every device.</item>
/// </list>
/// A <see cref="ReaderWriterLockSlim"/> lets the high-frequency readers (packet processing) run in parallel
/// while the rare writer (registration) gets exclusive access.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<MacKey, SensorDevice> _devices = new();
    private readonly Dictionary<SensorCategory, HashSet<MacKey>> _byCategory = new();
    private readonly Dictionary<string, HashSet<MacKey>> _bySubZone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<MacKey>> _tokens = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _devices.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public bool TryAdd(SensorDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        MacKey key = MacKey.Parse(device.MacAddress);

        _lock.EnterWriteLock();
        try
        {
            if (!_devices.TryAdd(key, device))
            {
                return false;
            }

            AddToIndex(_byCategory, device.Category, key);
            AddToIndex(_bySubZone, device.Location.SubZoneId, key);
            foreach (string token in Tokenize(IndexText(device)))
            {
                AddToIndex(_tokens, token, key);
            }

            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Contains(string mac) => MacKey.TryParse(mac, out MacKey key) && Contains(key);

    public bool Contains(MacKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _devices.ContainsKey(key);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>O(1) lookup by MAC address (any common format).</summary>
    public bool TryGet(string? mac, [NotNullWhen(true)] out SensorDevice? device)
    {
        device = null;
        return MacKey.TryParse(mac, out MacKey key) && TryGet(key, out device);
    }

    public bool TryGet(MacKey key, [NotNullWhen(true)] out SensorDevice? device)
    {
        _lock.EnterReadLock();
        try
        {
            return _devices.TryGetValue(key, out device);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<SensorDevice> All()
    {
        _lock.EnterReadLock();
        try
        {
            return Sort(_devices.Values);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<SensorDevice> InSubZone(string subZoneId)
    {
        _lock.EnterReadLock();
        try
        {
            return _bySubZone.TryGetValue(subZoneId, out HashSet<MacKey>? keys) ? Resolve(keys) : Array.Empty<SensorDevice>();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<SensorDevice> InCategory(SensorCategory category)
    {
        _lock.EnterReadLock();
        try
        {
            return _byCategory.TryGetValue(category, out HashSet<MacKey>? keys) ? Resolve(keys) : Array.Empty<SensorDevice>();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public int? ExpectedIntervalFor(string mac) => TryGet(mac, out SensorDevice? device) ? device.ExpectedIntervalSeconds : null;

    /// <summary>
    /// Finds devices matching every word of <paramref name="query"/> (prefix match, e.g. "pum a" finds "Pump A")
    /// and the optional category. A full MAC address short-circuits to a single dictionary lookup.
    /// </summary>
    public IReadOnlyList<SensorDevice> Search(string? query, SensorCategory? category, out string strategy)
    {
        string trimmed = (query ?? string.Empty).Trim();

        if (MacKey.TryParse(trimmed, out MacKey macKey))
        {
            strategy = "Exact MAC: Dictionary<MacKey, SensorDevice> O(1) lookup";
            if (TryGet(macKey, out SensorDevice? exact) && (category is null || exact.Category == category))
            {
                return new[] { exact };
            }

            return Array.Empty<SensorDevice>();
        }

        string[] words = Tokenize(trimmed).ToArray();

        _lock.EnterReadLock();
        try
        {
            HashSet<MacKey>? candidates = null;

            if (category is SensorCategory wanted)
            {
                candidates = _byCategory.TryGetValue(wanted, out HashSet<MacKey>? inCategory)
                    ? new HashSet<MacKey>(inCategory)
                    : new HashSet<MacKey>();
            }

            foreach (string word in words)
            {
                HashSet<MacKey> matches = MatchToken(word);
                if (candidates is null)
                {
                    candidates = matches;
                }
                else
                {
                    candidates.IntersectWith(matches);
                }

                if (candidates.Count == 0)
                {
                    break;
                }
            }

            if (candidates is null)
            {
                strategy = "No filter: all devices";
                return Sort(_devices.Values);
            }

            strategy = words.Length > 0
                ? "Inverted index: Dictionary<token, HashSet<MacKey>> + HashSet.IntersectWith"
                : "Category index: Dictionary<SensorCategory, HashSet<MacKey>>";
            return Resolve(candidates);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Lower-case words of a name, keeping hyphenated ids such as "b-01" together.</summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var current = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == ':')
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                yield return current.ToString().Trim('-', ':');
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim('-', ':');
        }
    }

    private static string IndexText(SensorDevice device) =>
        string.Join(' ',
            device.DisplayName,
            device.Metric.Replace('_', ' '),
            device.Category.ToString(),
            device.Location.SubZoneName,
            device.Location.ZoneName,
            device.Location.FacilityName,
            device.Location.Room ?? string.Empty,
            device.MacAddress,
            MacAddress.ToSlug(device.MacAddress));

    /// <summary>Exact token hit in O(1), plus the union of tokens that start with the word (two or more characters).</summary>
    private HashSet<MacKey> MatchToken(string word)
    {
        var result = new HashSet<MacKey>();
        if (word.Length == 0)
        {
            return result;
        }

        if (_tokens.TryGetValue(word, out HashSet<MacKey>? exact))
        {
            result.UnionWith(exact);
        }

        if (word.Length < 2)
        {
            return result; // single letters ("pump a") match exactly, otherwise "a" would match "air", "actuator", ...
        }

        foreach ((string token, HashSet<MacKey> keys) in _tokens)
        {
            if (token.Length > word.Length && token.StartsWith(word, StringComparison.Ordinal))
            {
                result.UnionWith(keys);
            }
        }

        return result;
    }

    private List<SensorDevice> Resolve(IEnumerable<MacKey> keys)
    {
        var devices = new List<SensorDevice>();
        foreach (MacKey key in keys)
        {
            if (_devices.TryGetValue(key, out SensorDevice? device))
            {
                devices.Add(device);
            }
        }

        return Sort(devices);
    }

    private static List<SensorDevice> Sort(IEnumerable<SensorDevice> devices) =>
        devices
            .OrderBy(d => d.Location.FacilityId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Location.SubZoneId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void AddToIndex<TKey>(Dictionary<TKey, HashSet<MacKey>> index, TKey key, MacKey mac) where TKey : notnull
    {
        if (!index.TryGetValue(key, out HashSet<MacKey>? set))
        {
            set = new HashSet<MacKey>();
            index[key] = set;
        }

        set.Add(mac);
    }
}
