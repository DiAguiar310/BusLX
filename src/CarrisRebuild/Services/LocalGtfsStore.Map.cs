using CarrisRebuild.Models;

namespace CarrisRebuild.Services;

// Filtros usados pelo mapa para responder a duas perguntas:
// "onde está este autocarro?" e "que autocarros vão para esta paragem?".
public sealed partial class LocalGtfsStore
{
    private readonly object _stopTripsLock = new();
    private readonly Dictionary<string, Dictionary<string, int[]>> _stopTripsCache =
        new(StringComparer.Ordinal);

    public Task<BusStopDto?> GetStopAsync(string stopId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_stopsByKey.TryGetValue(stopId, out var stop)) return null;
            return new BusStopDto(stop.StopId, stop.Number, stop.Name, stop.Lat, stop.Lng);
        }, ct);
    }

    public Task<IReadOnlyList<VehicleSnapshot>> GetVehiclesTowardsStopAsync(
        string stopId,
        IReadOnlyList<VehicleSnapshot> vehicles,
        CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<VehicleSnapshot>>(() =>
        {
            if (!_stopsByKey.TryGetValue(stopId, out var stop)) return [];

            Dictionary<string, int[]> sequences;
            lock (_stopTripsLock)
            {
                if (_stopTripsCache.TryGetValue(stop.StopId, out var cached))
                {
                    sequences = cached;
                }
                else
                {
                    var byTrip = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                    using (var db = Open())
                    {
                        foreach (var row in db.Query<StopTripSequenceRow>(
                            """
                            SELECT t.trip_key AS trip_key, st.seq AS seq
                            FROM stop_times st
                            JOIN trips t ON t.id = st.trip_id
                            WHERE st.stop_id = ?
                            """, stop.Id))
                        {
                            if (string.IsNullOrWhiteSpace(row.TripKey)) continue;
                            if (!byTrip.TryGetValue(row.TripKey, out var list))
                                byTrip[row.TripKey] = list = [];
                            list.Add(row.Sequence);
                        }
                    }

                    sequences = byTrip.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.ToArray(),
                        StringComparer.Ordinal);
                    _stopTripsCache[stop.StopId] = sequences;
                }
            }

            return vehicles
                .Where(v =>
                {
                    if (string.IsNullOrWhiteSpace(v.TripId)
                        || !sequences.TryGetValue(v.TripId, out var stopSequences))
                        return false;

                    var current = v.CurrentStopSequence;
                    if (current is null or <= 0) return true;
                    return stopSequences.Any(sequence => current.Value <= sequence);
                })
                .ToList();
        }, ct);
    }
}
