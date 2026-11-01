using System.Buffers.Binary;
using System.Text;
using CarrisRebuild.Models;

namespace CarrisRebuild.Services;

// Leitor mínimo de GTFS-Realtime (protobuf) para posições de veículos.
// Evita depender de bindings gerados e mantém a app pequena.
public static class GtfsRealtimeParser
{
    public static RealtimeFeed Parse(
        ReadOnlySpan<byte> payload,
        IReadOnlyDictionary<string, string> routeNumbersByRouteId,
        long nowEpoch)
    {
        long feedTimestamp = 0;
        var vehicles = new List<VehicleSnapshot>();
        var byTrip = new Dictionary<string, VehicleSnapshot>(StringComparer.Ordinal);

        var reader = new ProtoReader(payload);
        while (reader.Next(out var field, out var wire))
        {
            if (field == 1 && wire == 2)
                feedTimestamp = ParseHeader(reader.Bytes());
            else if (field == 2 && wire == 2)
            {
                var vehicle = ParseEntity(reader.Bytes(), routeNumbersByRouteId, feedTimestamp, nowEpoch);
                if (vehicle is not null)
                {
                    vehicles.Add(vehicle);
                    if (!string.IsNullOrEmpty(vehicle.TripId))
                        byTrip[vehicle.TripId] = vehicle;
                }
            }
            else
                reader.Skip(wire);
        }

        return new RealtimeFeed
        {
            FeedTimestamp = feedTimestamp,
            Vehicles = vehicles,
            ByTrip = byTrip,
        };
    }

    private static long ParseHeader(ReadOnlySpan<byte> data)
    {
        long timestamp = 0;
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            if (field == 3 && wire == 0)
                timestamp = (long)reader.Varint();
            else
                reader.Skip(wire);
        }
        return timestamp;
    }

    private static VehicleSnapshot? ParseEntity(
        ReadOnlySpan<byte> data,
        IReadOnlyDictionary<string, string> routeNumbersByRouteId,
        long feedTimestamp,
        long nowEpoch)
    {
        var vehicleData = ReadOnlySpan<byte>.Empty;
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            if (field == 4 && wire == 2)
                vehicleData = reader.Bytes();
            else
                reader.Skip(wire);
        }

        if (vehicleData.IsEmpty)
            return null;

        return ParseVehicle(vehicleData, routeNumbersByRouteId, feedTimestamp, nowEpoch);
    }

    private static VehicleSnapshot? ParseVehicle(
        ReadOnlySpan<byte> data,
        IReadOnlyDictionary<string, string> routeNumbersByRouteId,
        long feedTimestamp,
        long nowEpoch)
    {
        string tripId = "";
        string routeId = "";
        int directionId = 0;
        string vehicleId = "";
        string licensePlate = "";
        string nextStop = "";
        int? currentStopSequence = null;
        long timestamp = 0;
        double lat = 0;
        double lng = 0;
        double? bearing = null;
        double? speed = null;
        bool hasPosition = false;

        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    ParseTrip(reader.Bytes(), ref tripId, ref routeId, ref directionId);
                    break;
                case 2 when wire == 2:
                    ParsePosition(reader.Bytes(), ref lat, ref lng, ref bearing, ref speed, ref hasPosition);
                    break;
                case 3 when wire == 0:
                    currentStopSequence = (int)reader.Varint();
                    break;
                case 5 when wire == 0:
                    timestamp = (long)reader.Varint();
                    break;
                case 7 when wire == 2:
                    nextStop = reader.String();
                    break;
                case 8 when wire == 2:
                    ParseVehicleDescriptor(reader.Bytes(), ref vehicleId, ref licensePlate);
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (!hasPosition)
            return null;
        if (timestamp > 0 && nowEpoch - timestamp > 300)
            return null;

        var routeNumber = routeNumbersByRouteId.TryGetValue(routeId, out var number)
            ? number
            : routeId;
        if (string.IsNullOrWhiteSpace(vehicleId))
            vehicleId = licensePlate;

        return new VehicleSnapshot(
            VehicleId: vehicleId,
            RouteNumber: routeNumber,
            RouteId: routeId,
            TripId: tripId,
            Lat: lat,
            Lng: lng,
            Bearing: bearing,
            Speed: speed,
            NextStop: nextStop,
            CurrentStopSequence: currentStopSequence,
            Direction: directionId == 0 ? "Ida" : "Volta",
            Timestamp: timestamp > 0 ? timestamp : feedTimestamp,
            FeedUpdated: feedTimestamp);
    }

    private static void ParseTrip(
        ReadOnlySpan<byte> data,
        ref string tripId,
        ref string routeId,
        ref int directionId)
    {
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    tripId = reader.String();
                    break;
                case 5 when wire == 2:
                    routeId = reader.String();
                    break;
                case 6 when wire == 0:
                    directionId = (int)reader.Varint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }
    }

    private static void ParseVehicleDescriptor(
        ReadOnlySpan<byte> data,
        ref string vehicleId,
        ref string licensePlate)
    {
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    vehicleId = reader.String();
                    break;
                case 3 when wire == 2:
                    licensePlate = reader.String();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }
    }

    private static void ParsePosition(
        ReadOnlySpan<byte> data,
        ref double lat,
        ref double lng,
        ref double? bearing,
        ref double? speed,
        ref bool hasPosition)
    {
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 5:
                    lat = reader.Float();
                    hasPosition = true;
                    break;
                case 2 when wire == 5:
                    lng = reader.Float();
                    hasPosition = true;
                    break;
                case 3 when wire == 5:
                    bearing = reader.Float();
                    break;
                case 5 when wire == 5:
                    speed = reader.Float();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }
    }

    private ref struct ProtoReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public bool Next(out int field, out int wire)
        {
            field = 0;
            wire = 0;
            if (_position >= _data.Length)
                return false;

            var tag = Varint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return field > 0;
        }

        public ulong Varint()
        {
            ulong value = 0;
            var shift = 0;
            while (_position < _data.Length && shift < 64)
            {
                var b = _data[_position++];
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                    return value;
                shift += 7;
            }
            throw new InvalidDataException("GTFS-RT varint inválido.");
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var length = checked((int)Varint());
            if (length < 0 || _position + length > _data.Length)
                throw new InvalidDataException("GTFS-RT campo truncado.");
            var result = _data.Slice(_position, length);
            _position += length;
            return result;
        }

        public string String() => Encoding.UTF8.GetString(Bytes());

        public float Float()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
            _position += 4;
            return BitConverter.UInt32BitsToSingle(value);
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0:
                    Varint();
                    break;
                case 1:
                    Ensure(8);
                    _position += 8;
                    break;
                case 2:
                    var length = checked((int)Varint());
                    Ensure(length);
                    _position += length;
                    break;
                case 5:
                    Ensure(4);
                    _position += 4;
                    break;
                default:
                    throw new InvalidDataException($"GTFS-RT wire type {wire} não suportado.");
            }
        }

        private void Ensure(int count)
        {
            if (_position + count > _data.Length)
                throw new InvalidDataException("GTFS-RT campo truncado.");
        }
    }
}
