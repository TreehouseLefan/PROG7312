using SmartX.Core.Contracts;
using SmartX.Core.Pulse;

namespace SmartX.Api.Services;

/// <summary>
/// Groups repeated anomaly events into acknowledgeable incidents (alert-fatigue control):
/// a device that spikes again while an incident is open, or within a minute of it closing,
/// bumps the existing incident instead of creating a new alert.
/// </summary>
public sealed class IncidentBook
{
    private static readonly TimeSpan RegroupWindow = TimeSpan.FromSeconds(60);
    private const int MaxHistory = 500;

    private readonly object _gate = new();
    private readonly List<Incident> _incidents = new();

    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return _incidents.Count(i => i.ClosedAt is null);
            }
        }
    }

    public void Raise(SensorDevice device, IncidentKind kind, DateTimeOffset now)
    {
        lock (_gate)
        {
            Incident? existing = null;
            for (int i = _incidents.Count - 1; i >= 0; i--)
            {
                Incident candidate = _incidents[i];
                if (candidate.Kind != kind ||
                    !string.Equals(candidate.DeviceMac, device.MacAddress, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (candidate.ClosedAt is null || now - candidate.ClosedAt.Value <= RegroupWindow)
                {
                    existing = candidate;
                }

                break; // only the most recent incident of this kind matters
            }

            if (existing is not null)
            {
                existing.Occurrences++;
                existing.LastEventAt = now;
                existing.ClosedAt = null;
                return;
            }

            _incidents.Add(new Incident
            {
                Id = Guid.NewGuid(),
                DeviceMac = device.MacAddress,
                DeviceName = device.DisplayName,
                SubZoneId = device.Location.SubZoneId,
                Kind = kind,
                OpenedAt = now,
                LastEventAt = now,
                Occurrences = 1
            });

            TrimHistory();
        }
    }

    public void ResolveAll(string mac, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (Incident incident in _incidents)
            {
                if (incident.ClosedAt is null &&
                    string.Equals(incident.DeviceMac, mac, StringComparison.OrdinalIgnoreCase))
                {
                    incident.ClosedAt = now;
                }
            }
        }
    }

    public IncidentDto? GetOpenFor(string mac)
    {
        lock (_gate)
        {
            for (int i = _incidents.Count - 1; i >= 0; i--)
            {
                Incident incident = _incidents[i];
                if (incident.ClosedAt is null &&
                    string.Equals(incident.DeviceMac, mac, StringComparison.OrdinalIgnoreCase))
                {
                    return incident.ToDto();
                }
            }

            return null;
        }
    }

    public IncidentDto[] List(bool openOnly, string? mac = null, int max = 200)
    {
        lock (_gate)
        {
            IEnumerable<Incident> query = _incidents;
            if (openOnly)
            {
                query = query.Where(i => i.ClosedAt is null);
            }

            if (!string.IsNullOrWhiteSpace(mac))
            {
                query = query.Where(i => string.Equals(i.DeviceMac, mac, StringComparison.OrdinalIgnoreCase));
            }

            return query
                .OrderByDescending(i => i.LastEventAt)
                .Take(Math.Clamp(max, 1, 500))
                .Select(i => i.ToDto())
                .ToArray();
        }
    }

    public IncidentDto? Acknowledge(Guid id, string? acknowledgedBy, DateTimeOffset now)
    {
        lock (_gate)
        {
            Incident? incident = _incidents.FirstOrDefault(i => i.Id == id);
            if (incident is null)
            {
                return null;
            }

            if (incident.AcknowledgedAt is null)
            {
                incident.AcknowledgedAt = now;
                incident.AcknowledgedBy = string.IsNullOrWhiteSpace(acknowledgedBy) ? "operator" : acknowledgedBy.Trim();
            }

            return incident.ToDto();
        }
    }

    /// <summary>Median time-to-acknowledge: an engagement metric shown on the dashboard.</summary>
    public double? MedianSecondsToAcknowledge()
    {
        lock (_gate)
        {
            double[] values = _incidents
                .Where(i => i.AcknowledgedAt is not null)
                .Select(i => (i.AcknowledgedAt!.Value - i.OpenedAt).TotalSeconds)
                .OrderBy(v => v)
                .ToArray();

            if (values.Length == 0)
            {
                return null;
            }

            int middle = values.Length / 2;
            double median = values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
            return Math.Round(median, 1);
        }
    }

    private void TrimHistory()
    {
        while (_incidents.Count > MaxHistory)
        {
            int index = _incidents.FindIndex(i => i.ClosedAt is not null);
            if (index < 0)
            {
                break;
            }

            _incidents.RemoveAt(index);
        }
    }

    private sealed class Incident
    {
        public Guid Id { get; init; }
        public string DeviceMac { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public string SubZoneId { get; init; } = string.Empty;
        public IncidentKind Kind { get; init; }
        public DateTimeOffset OpenedAt { get; init; }
        public DateTimeOffset LastEventAt { get; set; }
        public int Occurrences { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }
        public string? AcknowledgedBy { get; set; }
        public DateTimeOffset? AcknowledgedAt { get; set; }

        public IncidentDto ToDto() => new(
            Id,
            DeviceMac,
            DeviceName,
            SubZoneId,
            Kind,
            OpenedAt,
            LastEventAt,
            Occurrences,
            ClosedAt is null,
            ClosedAt,
            AcknowledgedAt is not null,
            AcknowledgedBy,
            AcknowledgedAt,
            AcknowledgedAt is null ? (double?)null : Math.Round((AcknowledgedAt.Value - OpenedAt).TotalSeconds, 1));
    }
}
