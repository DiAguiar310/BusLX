using System.Globalization;
using System.Text.RegularExpressions;
using CarrisRebuild.Models;

namespace CarrisRebuild.Services;

public sealed partial class LocalGtfsStore
{
    public Task<IReadOnlyList<BusStopDto>> GetBusStopsAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<BusStopDto>>(() =>
            _stops.Select(s => new BusStopDto(s.StopId, s.Number, s.Name, s.Lat, s.Lng)).ToList(), ct);
    }

    public Task<IReadOnlyList<RouteDto>> GetRoutesAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<RouteDto>>(() =>
            _sortedRoutes.Select(r => new RouteDto(r.Number, r.Name, r.Direction)).ToList(), ct);
    }

    public Task<FullRouteDto> GetRouteAsync(string number, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_routesByNumber.TryGetValue(number, out var info))
                return new FullRouteDto(number, [], []);
            var variants = info.Variants
                .Select(v => new VariantDto(v.RouteKey, v.Headsign))
                .ToList();
            return new FullRouteDto(number, variants, GetRouteDirections(number));
        }, ct);
    }

    public Task<IReadOnlyList<RouteDto>> GetRoutesForBusStopAsync(
        string stopId,
        CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<RouteDto>>(() =>
        {
            if (!_stopsByKey.TryGetValue(stopId, out var stop))
                return [];
            using var db = Open();
            var active = ActiveServices(NowLisbon().Date);
            if (active.Count == 0) return [];

            var placeholders = string.Join(",", active.Select(_ => "?"));
            var args = new List<object> { stop.Id };
            args.AddRange(active.Cast<object>());
            var rows = db.Query<StopRouteRow>(
                $"""
                 SELECT DISTINCT t.route_id AS route_id
                 FROM stop_times st
                 JOIN trips t ON t.id = st.trip_id
                 WHERE st.stop_id = ? AND t.service_id IN ({placeholders})
                 """, args.ToArray());

            var numbers = rows
                .Select(r => _routeNumbersByInt.GetValueOrDefault(r.RouteId))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => _routesByNumber.GetValueOrDefault(n!))
                .Where(r => r is not null)
                .Select(r => new RouteDto(r!.Number, r.Name, r.Direction))
                .ToList();

            return numbers
                .OrderBy(r => r.Number.Length)
                .ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    public Task<IReadOnlyList<StopTimeDto>> GetStopTimesAsync(
        string route,
        string direction,
        string stop,
        string date,
        CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<StopTimeDto>>(() =>
        {
            var day = TryParseDate(date) ?? NowLisbon().Date;
            if (!_stopsByKey.TryGetValue(stop, out var stopInfo)) return [];
            using var db = Open();
            var active = ActiveServices(day);
            if (active.Count == 0) return [];

            var placeholders = string.Join(",", active.Select(_ => "?"));
            var args = new List<object> { stopInfo.Id, route };
            args.AddRange(active.Cast<object>());
            var rows = db.Query<StopTimeRow>(
                $"""
                 SELECT st.dep AS dep, t.direction AS direction, r.short_name AS short_name
                 FROM stop_times st
                 JOIN trips t ON t.id = st.trip_id
                 JOIN routes r ON r.id = t.route_id
                 WHERE st.stop_id = ? AND r.short_name = ?
                   AND t.service_id IN ({placeholders})
                 ORDER BY st.dep
                 LIMIT 300
                 """, args.ToArray());

            var wanted = (direction ?? "").Trim().ToLowerInvariant();
            var result = new List<StopTimeDto>();
            foreach (var row in rows)
            {
                var dir = row.Direction == 0 ? "Ida" : "Volta";
                if (wanted is "0" or "ida" && dir != "Ida") continue;
                if (wanted is "1" or "volta" && dir != "Volta") continue;
                result.Add(new StopTimeDto(FormatGtfsTime(row.DepartureSec), row.ShortName ?? route));
            }
            return result;
        }, ct);
    }

    internal Task<LocalShape> GetShapeAsync(
        string routeNumber,
        string direction,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var db = Open();
            var directionFilter = direction.ToLowerInvariant() switch
            {
                "0" or "ida" => " AND t.direction = 0",
                "1" or "volta" => " AND t.direction = 1",
                _ => "",
            };
            var shape = db.Query<ShapeChoiceRow>(
                $"""
                 SELECT t.shape_id AS shape_id, COUNT(*) AS c
                 FROM trips t
                 JOIN routes r ON r.id = t.route_id
                 WHERE r.short_name = ? AND t.shape_id != '' {directionFilter}
                 GROUP BY t.shape_id
                 ORDER BY c DESC
                 LIMIT 1
                 """, routeNumber).FirstOrDefault();
            if (shape is null || string.IsNullOrWhiteSpace(shape.ShapeId))
                return new LocalShape(routeNumber, "", [], []);

            var points = db.Query<ShapePointRow>(
                "SELECT lat, lon FROM shapes WHERE shape_id = ? ORDER BY seq",
                shape.ShapeId)
                .Select(p => new[] { p.Lat, p.Lng })
                .ToList();

            var stops = new List<LocalShapeStop>();
            var trip = db.Query<TripChoiceRow>(
                """
                SELECT t.id
                FROM trips t
                JOIN routes r ON r.id = t.route_id
                WHERE r.short_name = ? AND t.shape_id = ?
                LIMIT 1
                """, routeNumber, shape.ShapeId).FirstOrDefault();
            if (trip is not null)
            {
                stops = db.Query<ShapeStopRow>(
                    """
                    SELECT s.stop_id AS stop_id, s.name AS name, s.lat AS lat, s.lon AS lon
                    FROM stop_times st
                    JOIN stops s ON s.id = st.stop_id
                    WHERE st.trip_id = ?
                    ORDER BY st.seq
                    """, trip.Id)
                    .Select(s => new LocalShapeStop(
                        s.StopKey ?? "", s.Name ?? "", s.Lat, s.Lng))
                    .ToList();
            }

            return new LocalShape(routeNumber, shape.ShapeId, points, stops);
        }, ct);
    }

    private List<RouteDirectionDto> GetRouteDirections(string number)
    {
        using var db = Open();
        var rows = db.Query<TripEndRow>(
            """
            SELECT t.direction AS direction,
                   (SELECT s.name FROM stop_times st
                    JOIN stops s ON s.id = st.stop_id
                    WHERE st.trip_id = t.id ORDER BY st.seq LIMIT 1) AS origin,
                   (SELECT s.name FROM stop_times st
                    JOIN stops s ON s.id = st.stop_id
                    WHERE st.trip_id = t.id ORDER BY st.seq DESC LIMIT 1) AS destination,
                   COUNT(*) AS trips
            FROM trips t
            JOIN routes r ON r.id = t.route_id
            WHERE r.short_name = ?
            GROUP BY t.direction, origin, destination
            ORDER BY t.direction, trips DESC
            """, number);

        var best = new Dictionary<int, TripEndRow>();
        foreach (var row in rows)
            best.TryAdd(row.Direction, row);

        var result = new List<RouteDirectionDto>();
        foreach (var directionId in best.Keys.OrderBy(k => k))
        {
            var row = best[directionId];
            var origin = string.IsNullOrWhiteSpace(row.Origin) ? number : row.Origin!;
            var destination = string.IsNullOrWhiteSpace(row.Destination) ? origin : row.Destination!;
            var circular = string.Equals(origin, destination, StringComparison.Ordinal);
            var label = circular ? $"Circulação · {origin}" : $"{origin} → {destination}";
            var shortLabel = circular
                ? "Circulação"
                : Regex.Replace(destination, @"\s*\([^)]*\)\s*$", "").Trim();
            if (shortLabel.Length == 0) shortLabel = destination;
            result.Add(new RouteDirectionDto(
                directionId == 0 ? "Ida" : "Volta",
                directionId,
                origin,
                destination,
                label,
                shortLabel,
                circular));
        }
        return result;
    }

    private HashSet<string> ActiveServices(DateTime day)
    {
        using var db = Open();
        var ymd = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var weekday = day.DayOfWeek switch
        {
            DayOfWeek.Monday => "mon",
            DayOfWeek.Tuesday => "tue",
            DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu",
            DayOfWeek.Friday => "fri",
            DayOfWeek.Saturday => "sat",
            _ => "sun",
        };

        var active = db.Query<CalendarRow>(
                $"SELECT * FROM calendar WHERE start_date <= ? AND end_date >= ? AND {weekday} = 1",
                ymd, ymd)
            .Select(r => r.ServiceId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in db.Query<CalendarDateRow>(
                     "SELECT service_id, exception FROM calendar_dates WHERE date = ?", ymd))
        {
            if (string.IsNullOrWhiteSpace(row.ServiceId)) continue;
            if (row.Exception == 1) active.Add(row.ServiceId);
            else if (row.Exception == 2) active.Remove(row.ServiceId);
        }
        return active;
    }

    private static DateTime? TryParseDate(string value)
        => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date.Date
            : null;

    private static string FormatGtfsTime(int seconds)
    {
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        return $"{hours:00}:{minutes:00}";
    }
}
