using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services;

/// <summary>
/// Historical logs ordered by timestamp.
/// <list type="bullet">
/// <item>One <see cref="TimelineStore{T}"/> (a capped <c>SortedDictionary</c>) per device, held in a
/// <c>Dictionary&lt;MacKey, ...&gt;</c>, feeds the timeline graphs.</item>
/// <item>One global store records operator and system events (commands, undo, alerts, searches) for the
/// "History" panel.</item>
/// </list>
/// </summary>
public sealed class TimelineService
{
    public const int PointsPerDevice = 3_000;

    private readonly object _gate = new();
    private readonly Dictionary<MacKey, TimelineStore<double>> _devices = new();
    private readonly TimelineStore<HistoryEventDto> _history = new(5_000);
    private long _historyVersion;

    public long HistoryVersion => Interlocked.Read(ref _historyVersion);

    public void Record(MacKey device, DateTimeOffset timestamp, double value) => GetOrAdd(device).Add(timestamp, value);

    public bool TryGet(MacKey device, out TimelineStore<double>? store)
    {
        lock (_gate)
        {
            return _devices.TryGetValue(device, out store);
        }
    }

    public List<(DateTimeOffset Timestamp, double Value)> Range(MacKey device, DateTimeOffset from, DateTimeOffset to) =>
        TryGet(device, out TimelineStore<double>? store) && store is not null
            ? store.Range(from, to)
            : new List<(DateTimeOffset, double)>();

    public void AddHistory(HistoryKind kind, string title, string detail, string? mac, DateTimeOffset at)
    {
        _history.Add(at, new HistoryEventDto(at, kind, title, detail, mac));
        Interlocked.Increment(ref _historyVersion);
    }

    /// <summary>Newest events first, optionally filtered by kind.</summary>
    public HistoryEventDto[] History(DateTimeOffset from, DateTimeOffset to, HistoryKind? kind, int max)
    {
        List<(DateTimeOffset Timestamp, HistoryEventDto Value)> range = _history.Range(from, to);
        var result = new List<HistoryEventDto>(Math.Min(max, range.Count));

        for (int i = range.Count - 1; i >= 0 && result.Count < max; i--)
        {
            HistoryEventDto item = range[i].Value;
            if (kind is null || item.Kind == kind)
            {
                result.Add(item);
            }
        }

        return result.ToArray();
    }

    private TimelineStore<double> GetOrAdd(MacKey device)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(device, out TimelineStore<double>? store))
            {
                store = new TimelineStore<double>(PointsPerDevice);
                _devices[device] = store;
            }

            return store;
        }
    }
}
