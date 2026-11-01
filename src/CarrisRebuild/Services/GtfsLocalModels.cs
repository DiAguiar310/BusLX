using SQLite;

namespace CarrisRebuild.Services;

internal sealed class LocalStop
{
    public required int Id { get; init; }
    public required string StopId { get; init; }
    public required string Number { get; init; }
    public required string Name { get; init; }
    public required double Lat { get; init; }
    public required double Lng { get; init; }
    public required string Normalized { get; init; }
    public required IReadOnlySet<string> Tokens { get; init; }
}

internal sealed class LocalRouteVariant
{
    public required int RouteId { get; init; }
    public required string RouteKey { get; init; }
    public required string Headsign { get; init; }
    public required string Direction { get; init; }
    public int Trips { get; init; }
}

internal sealed class LocalRouteInfo
{
    public required string Number { get; init; }
    public required string Name { get; init; }
    public required string Direction { get; init; }
    public required string Color { get; init; }
    public required string RouteKey { get; init; }
    public List<LocalRouteVariant> Variants { get; } = [];
}

internal sealed record LocalShape(
    string RouteNumber,
    string ShapeId,
    IReadOnlyList<double[]> Points,
    IReadOnlyList<LocalShapeStop> Stops);

internal sealed record LocalShapeStop(string Id, string Name, double Lat, double Lng);

[Table("stops")]
internal sealed class StopRow
{
    [PrimaryKey, Column("id")] public int Id { get; set; }
    [Column("stop_id")] public string? StopKey { get; set; }
    [Column("code")] public string? Code { get; set; }
    [Column("name")] public string? Name { get; set; }
    [Column("lat")] public double Lat { get; set; }
    [Column("lon")] public double Lng { get; set; }
}

internal sealed class RouteSummaryRow
{
    [Column("route_int")] public int RouteInt { get; set; }
    [Column("route_id")] public string? RouteKey { get; set; }
    [Column("short_name")] public string? ShortName { get; set; }
    [Column("long_name")] public string? LongName { get; set; }
    [Column("color")] public string? Color { get; set; }
    [Column("trips")] public int Trips { get; set; }
    [Column("directions")] public string? Directions { get; set; }
}

internal sealed class TripEndRow
{
    [Column("direction")] public int Direction { get; set; }
    [Column("origin")] public string? Origin { get; set; }
    [Column("destination")] public string? Destination { get; set; }
    [Column("headsign")] public string? Headsign { get; set; }
    [Column("trips")] public int Trips { get; set; }
}

internal sealed class DepartureRow
{
    [Column("trip_id")] public int TripId { get; set; }
    [Column("trip_key")] public string? TripKey { get; set; }
    [Column("stop_id")] public int StopId { get; set; }
    [Column("dep")] public int DepartureSec { get; set; }
    [Column("arr")] public int ArrivalSec { get; set; }
    [Column("seq")] public int StopSequence { get; set; }
    [Column("route_id")] public int RouteId { get; set; }
    [Column("direction")] public int Direction { get; set; }
    [Column("headsign")] public string? Headsign { get; set; }
    [Column("service_id")] public string? ServiceId { get; set; }
    [Column("short_name")] public string? ShortName { get; set; }
    [Column("long_name")] public string? LongName { get; set; }
}

internal sealed class StopTimeRow
{
    [Column("dep")] public int DepartureSec { get; set; }
    [Column("direction")] public int Direction { get; set; }
    [Column("short_name")] public string? ShortName { get; set; }
}

internal sealed class StopRouteRow
{
    [Column("route_id")] public int RouteId { get; set; }
}

internal sealed class StopTripSequenceRow
{
    [Column("trip_key")] public string? TripKey { get; set; }
    [Column("seq")] public int Sequence { get; set; }
}

internal sealed class ShapeChoiceRow
{
    [Column("shape_id")] public string? ShapeId { get; set; }
    [Column("c")] public int Count { get; set; }
}

internal sealed class ShapePointRow
{
    [Column("lat")] public double Lat { get; set; }
    [Column("lon")] public double Lng { get; set; }
}

internal sealed class TripChoiceRow
{
    [Column("id")] public int Id { get; set; }
}

internal sealed class ShapeStopRow
{
    [Column("stop_id")] public string? StopKey { get; set; }
    [Column("name")] public string? Name { get; set; }
    [Column("lat")] public double Lat { get; set; }
    [Column("lon")] public double Lng { get; set; }
}

internal sealed class DirectTripRow
{
    [Column("trip_id")] public int TripId { get; set; }
    [Column("origin_stop")] public int OriginStop { get; set; }
    [Column("dep")] public int DepartureSec { get; set; }
    [Column("oseq")] public int OriginSequence { get; set; }
    [Column("dest_stop")] public int DestStop { get; set; }
    [Column("arr")] public int ArrivalSec { get; set; }
    [Column("dseq")] public int DestSequence { get; set; }
    [Column("route_id")] public int RouteId { get; set; }
    [Column("direction")] public int Direction { get; set; }
    [Column("headsign")] public string? Headsign { get; set; }
    [Column("short_name")] public string? ShortName { get; set; }
    [Column("long_name")] public string? LongName { get; set; }
}

internal sealed class TransferTripRow
{
    [Column("trip1")] public int Trip1 { get; set; }
    [Column("origin_stop")] public int OriginStop { get; set; }
    [Column("dep1")] public int Departure1 { get; set; }
    [Column("transfer_stop")] public int TransferStop { get; set; }
    [Column("arrx")] public int ArrivalTransfer { get; set; }
    [Column("trip2")] public int Trip2 { get; set; }
    [Column("depx2")] public int DepartureTransfer { get; set; }
    [Column("dest_stop")] public int DestStop { get; set; }
    [Column("arr2")] public int Arrival2 { get; set; }
    [Column("dir1")] public int Direction1 { get; set; }
    [Column("head1")] public string? Headsign1 { get; set; }
    [Column("dir2")] public int Direction2 { get; set; }
    [Column("head2")] public string? Headsign2 { get; set; }
    [Column("route1")] public string? Route1 { get; set; }
    [Column("name1")] public string? Name1 { get; set; }
    [Column("route2")] public string? Route2 { get; set; }
    [Column("name2")] public string? Name2 { get; set; }
}

internal sealed class CalendarRow
{
    [Column("service_id")] public string? ServiceId { get; set; }
    [Column("mon")] public int Monday { get; set; }
    [Column("tue")] public int Tuesday { get; set; }
    [Column("wed")] public int Wednesday { get; set; }
    [Column("thu")] public int Thursday { get; set; }
    [Column("fri")] public int Friday { get; set; }
    [Column("sat")] public int Saturday { get; set; }
    [Column("sun")] public int Sunday { get; set; }
    [Column("start_date")] public string? StartDate { get; set; }
    [Column("end_date")] public string? EndDate { get; set; }
}

internal sealed class CalendarDateRow
{
    [Column("service_id")] public string? ServiceId { get; set; }
    [Column("exception")] public int Exception { get; set; }
}
