// As 18 operações do IUrlManager observado, como interface limpa.
using CarrisRebuild.Models;

namespace CarrisRebuild.Services;

public interface ICarrisApi
{
    Task<IReadOnlyList<BusStopDto>> GetBusStopsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RouteDto>> GetRoutesAsync(CancellationToken ct = default);
    Task<FullRouteDto> GetRouteAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<RouteDto>> GetRoutesForBusStopAsync(string stopId, CancellationToken ct = default);
    Task<IReadOnlyList<EstimationDto>> GetEstimationsAsync(string stopId, CancellationToken ct = default);
    Task<IReadOnlyList<StopTimeDto>> GetStopTimesAsync(string route, string dir, string stop, string date, CancellationToken ct = default);
    Task<IReadOnlyList<VehicleStatusDto>> GetVehicleStatusesAsync(string route, CancellationToken ct = default);
}
