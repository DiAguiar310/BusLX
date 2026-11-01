using System.Globalization;
using CarrisRebuild.Models;

namespace CarrisRebuild.Services;

public sealed partial class LocalGtfsStore
{
    public Task<IReadOnlyList<EstimationDto>> GetEstimationsAsync(
        string stopId,
        IReadOnlyDictionary<string, VehicleSnapshot> vehiclesByTrip,
        CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<EstimationDto>>(() =>
        {
            if (!_stopsByKey.TryGetValue(stopId, out var stop))
                return [];

            var current = NowLisbon();
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
            var nowSec = current.Hour * 3600 + current.Minute * 60 + current.Second;
            var candidates = new List<(DateTimeOffset When, DepartureRow Row)>();

            // Inclui partidas recentes já atrasadas: um autocarro real pode
            // ainda estar a caminho mesmo depois da hora prevista.
            AddCandidates(
                current.Date, ActiveServices(current.Date), Math.Max(0, nowSec - 2700),
                current, tz, stop.Id, candidates);
            AddCandidates(
                current.Date.AddDays(-1), ActiveServices(current.Date.AddDays(-1)), 86400,
                current, tz, stop.Id, candidates);
            AddCandidates(
                current.Date.AddDays(1), ActiveServices(current.Date.AddDays(1)), 0,
                current, tz, stop.Id, candidates);

            candidates.Sort((a, b) => a.When.CompareTo(b.When));
            var output = new List<EstimationDto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (when, row) in candidates)
            {
                var tripKey = row.TripKey ?? "";
                var key = $"{tripKey}|{row.StopId}|{when:O}";
                if (!seen.Add(key)) continue;

                var scheduledSec = Math.Max(0, (int)(when - current).TotalSeconds);
                var etaSec = scheduledSec;
                var realtime = false;
                var vehicleId = "";
                var passedStop = false;

                if (!string.IsNullOrEmpty(tripKey)
                    && vehiclesByTrip.TryGetValue(tripKey, out var vehicle))
                {
                    var seq = vehicle.CurrentStopSequence;
                    if (seq is > 0 && seq.Value > row.StopSequence)
                    {
                        // A posição real já passou esta paragem. Uma previsão
                        // futura para a mesma viagem seria um autocarro fantasma.
                        passedStop = true;
                    }
                    else
                    {
                        var distance = HaversineMeters(
                            vehicle.Lat, vehicle.Lng, stop.Lat, stop.Lng);
                        var speed = vehicle.Speed ?? 0;
                        if (speed < 4.0 || speed > 30.0) speed = 5.5;
                        var eta = distance / speed;
                        if (eta is >= 0 and <= 7200)
                        {
                            etaSec = (int)eta;
                            realtime = true;
                            vehicleId = vehicle.VehicleId;
                        }
                    }
                }

                if (passedStop || (!realtime && when < current))
                    continue;

                var etaMinutes = Math.Max(0, etaSec) / 60;
                var etaSeconds = Math.Max(0, etaSec) % 60;
                var route = row.ShortName ?? "";
                output.Add(new EstimationDto(
                    RouteNumber: route,
                    Headsign: FirstNonEmpty(row.Headsign, row.LongName, route),
                    Minutes: etaMinutes,
                    VehicleId: vehicleId,
                    Seconds: etaSeconds,
                    Realtime: realtime,
                    StopName: stop.Name,
                    Time: when.ToString("o", CultureInfo.InvariantCulture),
                    Direction: row.Direction == 0 ? "Ida" : "Volta"));
            }

            var ordered = output
                .OrderBy(e => e.Minutes * 60 + e.Seconds)
                .ThenBy(e => e.Time, StringComparer.Ordinal)
                .ToList();

            // O feed em tempo real pode associar o mesmo autocarro a mais do
            // que uma viagem. Mostrar as duas cria a ilusão de dois autocarros
            // quando o mapa só tem um; fica apenas a ocorrência mais próxima.
            var seenVehicles = new HashSet<string>(StringComparer.Ordinal);
            var realtimeEtas = new Dictionary<(string Route, string Direction, string Headsign), List<int>>();
            foreach (var estimation in ordered)
            {
                if (!estimation.Realtime) continue;
                var group = EstimationGroup(estimation);
                if (!realtimeEtas.TryGetValue(group, out var etas))
                    realtimeEtas[group] = etas = [];
                etas.Add(EstimationEtaSeconds(estimation));
            }

            var filtered = new List<EstimationDto>(ordered.Count);
            foreach (var estimation in ordered)
            {
                if (estimation.Realtime
                    && !string.IsNullOrWhiteSpace(estimation.VehicleId)
                    && !seenVehicles.Add(estimation.VehicleId))
                    continue;

                if (!estimation.Realtime
                    && realtimeEtas.TryGetValue(EstimationGroup(estimation), out var realtimeForRoute))
                {
                    // Uma partida apenas prevista que coincide com uma posição
                    // real é quase sempre a mesma viagem: o horário teórico e o
                    // autocarro atrasado. Não escondemos autocarros reais
                    // distintos, porque estes têm VehicleId e são mantidos.
                    var eta = EstimationEtaSeconds(estimation);
                    if (realtimeForRoute.Exists(rt => Math.Abs(rt - eta) <= 180))
                        continue;
                }

                filtered.Add(estimation);
            }

            return filtered.Take(10).ToList();
        }, ct);
    }

    private static (string Route, string Direction, string Headsign) EstimationGroup(
        EstimationDto estimation)
        => (estimation.RouteNumber, estimation.Direction ?? "", estimation.Headsign);

    private static int EstimationEtaSeconds(EstimationDto estimation)
        => estimation.Minutes * 60 + estimation.Seconds;

    private void AddCandidates(
        DateTime day,
        HashSet<string> active,
        int minSec,
        DateTimeOffset current,
        TimeZoneInfo tz,
        int stopId,
        List<(DateTimeOffset When, DepartureRow Row)> candidates)
    {
        if (active.Count == 0) return;
        using var db = Open();
        var placeholders = string.Join(",", active.Select(_ => "?"));
        var args = new List<object> { stopId, minSec };
        args.AddRange(active.Cast<object>());
        var rows = db.Query<DepartureRow>(
            $"""
             SELECT st.trip_id AS trip_id, t.trip_key AS trip_key, st.stop_id AS stop_id,
                    st.dep AS dep, st.arr AS arr, st.seq AS seq,
                    t.route_id AS route_id, t.direction AS direction,
                    t.headsign AS headsign, t.service_id AS service_id,
                    r.short_name AS short_name, r.long_name AS long_name
             FROM stop_times st
             JOIN trips t ON t.id = st.trip_id
             JOIN routes r ON r.id = t.route_id
             WHERE st.stop_id = ? AND st.dep >= ?
               AND t.service_id IN ({placeholders})
             ORDER BY st.dep
             LIMIT 120
             """, args.ToArray());

        var baseDate = day.Date;
        var baseOffset = tz.GetUtcOffset(baseDate);
        var baseWhen = new DateTimeOffset(baseDate, baseOffset);
        foreach (var row in rows)
        {
            var when = baseWhen.AddSeconds(row.DepartureSec);
            candidates.Add((when, row));
        }
    }
}
