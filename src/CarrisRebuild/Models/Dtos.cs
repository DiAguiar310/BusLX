// DTOs remodelados a partir dos contratos observados (XtpApiDtos.*). Sem código copiado.
namespace CarrisRebuild.Models;

public record BusStopDto(string Id, string Number, string Name, double Lat, double Lng);
public record RouteDto(string Number, string Name, string Direction);
public record RouteDirectionDto(
    string Direction,
    int DirectionId,
    string Origin,
    string Destination,
    string Label,
    string ShortLabel,
    bool Circular = false);
public record VariantDto(string Id, string Headsign);
public record FullRouteDto(
    string Number,
    IReadOnlyList<VariantDto> Variants,
    IReadOnlyList<RouteDirectionDto>? Directions = null);
public record EstimationDto(
    string RouteNumber,
    string Headsign,
    int Minutes,
    string VehicleId,
    int Seconds = 0,
    bool Realtime = false,
    string? StopName = null,
    string? Time = null,
    string? Direction = null)
{
    public string StatusText => Realtime ? "tempo real" : "previsto";
    public bool CanTrack => Realtime && !string.IsNullOrWhiteSpace(VehicleId);

    public string EtaText
    {
        get
        {
            if (Minutes <= 90) return $"{Minutes} min";
            if (DateTimeOffset.TryParse(Time, out var when))
                return when.ToString("HH:mm");
            return $"{Minutes} min";
        }
    }
}

public sealed record FavoriteStopCard(
    string Id,
    string Name,
    IReadOnlyList<EstimationDto> Next,
    bool Loading = false)
{
    public bool HasNext => Next.Count > 0;
    public bool NoNext => !Loading && !HasNext;
}
public record StopTimeDto(string Time, string RouteNumber);
public record VehicleStatusDto(string VehicleId, string RouteNumber, double Lat, double Lng);
