namespace CarrisRebuild.Models;

public sealed record VehicleSnapshot(
    string VehicleId,
    string RouteNumber,
    string RouteId,
    string TripId,
    double Lat,
    double Lng,
    double? Bearing,
    double? Speed,
    string NextStop,
    int? CurrentStopSequence,
    string Direction,
    long Timestamp,
    long FeedUpdated);

public sealed class RealtimeFeed
{
    public long FeedTimestamp { get; init; }
    public IReadOnlyList<VehicleSnapshot> Vehicles { get; init; } = [];
    public IReadOnlyDictionary<string, VehicleSnapshot> ByTrip { get; init; } =
        new Dictionary<string, VehicleSnapshot>();
}
