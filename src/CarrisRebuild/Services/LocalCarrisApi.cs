using CarrisRebuild.Models;

namespace CarrisRebuild.Services;

// Implementação local da API: toda a informação está na base GTFS embutida,
// exceto as posições em tempo real, que vêm diretamente da Carris.
public sealed class LocalCarrisApi(
    LocalGtfsStore store,
    GtfsRealtimeService realtime) : ICarrisApi
{
    public Task<IReadOnlyList<BusStopDto>> GetBusStopsAsync(CancellationToken ct = default)
        => store.GetBusStopsAsync(ct);

    public Task<IReadOnlyList<RouteDto>> GetRoutesAsync(CancellationToken ct = default)
        => store.GetRoutesAsync(ct);

    public Task<FullRouteDto> GetRouteAsync(string number, CancellationToken ct = default)
        => store.GetRouteAsync(number, ct);

    public Task<IReadOnlyList<RouteDto>> GetRoutesForBusStopAsync(
        string stopId,
        CancellationToken ct = default)
        => store.GetRoutesForBusStopAsync(stopId, ct);

    public async Task<IReadOnlyList<EstimationDto>> GetEstimationsAsync(
        string stopId,
        CancellationToken ct = default)
    {
        var feed = await realtime.GetFeedAsync(refreshIfStale: true, ct);
        return await store.GetEstimationsAsync(stopId, feed.ByTrip, ct);
    }

    public Task<IReadOnlyList<StopTimeDto>> GetStopTimesAsync(
        string route,
        string dir,
        string stop,
        string date,
        CancellationToken ct = default)
        => store.GetStopTimesAsync(route, dir, stop, date, ct);

    public async Task<IReadOnlyList<VehicleStatusDto>> GetVehicleStatusesAsync(
        string route,
        CancellationToken ct = default)
    {
        var vehicles = await realtime.GetVehiclesAsync(route, refreshIfStale: true, ct);
        return vehicles
            .Select(v => new VehicleStatusDto(v.VehicleId, v.RouteNumber, v.Lat, v.Lng))
            .ToList();
    }

}
