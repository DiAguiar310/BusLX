using System.Globalization;
using System.IO.Compression;
using System.Text;
using CarrisRebuild.Models;
using SQLite;

namespace CarrisRebuild.Services;

public sealed record BootstrapProgress(string Message, double Fraction);

// Base GTFS local. O ficheiro estático vai dentro do APK; as posições dos
// veículos são lidas diretamente do GTFS-RT público da Carris.
public sealed partial class LocalGtfsStore
{
    private const string SchemaVersion = "compact-1";
    private const string DatabaseAsset = "gtfs.db.gz";

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _dbTempPath;
    private bool _ready;
    private Dictionary<string, string> _routeNumbersByRouteId = new(StringComparer.Ordinal);
    private Dictionary<string, LocalRouteInfo> _routesByNumber = new(StringComparer.OrdinalIgnoreCase);
    private List<LocalRouteInfo> _sortedRoutes = [];
    private List<LocalStop> _stops = [];
    private Dictionary<string, LocalStop> _stopsByKey = new(StringComparer.Ordinal);
    private Dictionary<int, LocalStop> _stopsById = new();
    private Dictionary<int, string> _routeNumbersByInt = new();
    private Dictionary<string, List<string>> _variantIdsByRoute = new(StringComparer.OrdinalIgnoreCase);

    public LocalGtfsStore()
    {
        _root = Path.Combine(FileSystem.AppDataDirectory, "vialx");
        _dbPath = Path.Combine(_root, "gtfs.db");
        _dbTempPath = _dbPath + ".tmp";
    }

    public bool IsReady => _ready;
    public string DatabasePath => _dbPath;
    public string FeedVersion { get; private set; } = "";
    public string FeedStartDate { get; private set; } = "";
    public string FeedEndDate { get; private set; } = "";
    public IReadOnlyDictionary<string, string> RouteNumbersByRouteId => _routeNumbersByRouteId;
    internal IReadOnlyList<LocalStop> Stops => _stops;

    public async Task EnsureInitializedAsync(
        IProgress<BootstrapProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (_ready) return;

        await _initGate.WaitAsync(ct);
        try
        {
            if (_ready) return;
            Directory.CreateDirectory(_root);

            if (!IsDatabaseValid())
            {
                progress?.Report(new BootstrapProgress(
                    "A preparar os dados da rede Carris (primeira execução)…", 0.05));
                await RestorePackagedDatabaseAsync(progress, ct);
            }

            progress?.Report(new BootstrapProgress("A preparar índices locais…", 0.45));
            // A criação dos índices e o carregamento das caches são pesados
            // (milhões de linhas). Têm de correr fora da thread de UI para a
            // página de arranque continuar a responder e mostrar progresso.
            await Task.Run(() =>
            {
                EnsureIndexes(progress, ct);
                LoadCaches();
            }, ct);
            _ready = true;
            progress?.Report(new BootstrapProgress("Dados prontos.", 1.0));
        }
        finally
        {
            _initGate.Release();
        }
    }

    private bool IsDatabaseValid()
    {
        if (!File.Exists(_dbPath)) return false;
        try
        {
            using var db = Open();
            var version = db.QueryScalars<string>(
                "SELECT value FROM meta WHERE key = 'schema_version' LIMIT 1").FirstOrDefault();
            if (!string.Equals(version, SchemaVersion, StringComparison.Ordinal))
                return false;
            var stops = db.ExecuteScalar<int>("SELECT COUNT(*) FROM stops");
            var trips = db.ExecuteScalar<int>("SELECT COUNT(*) FROM trips");
            return stops > 100 && trips > 100;
        }
        catch
        {
            return false;
        }
    }

    private async Task RestorePackagedDatabaseAsync(
        IProgress<BootstrapProgress>? progress,
        CancellationToken ct)
    {
        if (File.Exists(_dbTempPath)) File.Delete(_dbTempPath);

        await using var asset = await FileSystem.OpenAppPackageFileAsync(DatabaseAsset);
        await using var gzip = new GZipStream(asset, CompressionMode.Decompress);
        await using (var output = new FileStream(
            _dbTempPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true))
        {
            var buffer = new byte[131072];
            long done = 0;
            int read;
            while ((read = await gzip.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (done % (8 * 1024 * 1024) < buffer.Length)
                {
                    var fraction = 0.05 + Math.Min(0.35, done / (66.0 * 1024 * 1024) * 0.35);
                    progress?.Report(new BootstrapProgress(
                        $"A instalar a base GTFS… {done / (1024 * 1024)} MB", fraction));
                }
            }
        }

        File.Move(_dbTempPath, _dbPath, true);
        progress?.Report(new BootstrapProgress("Base GTFS instalada.", 0.42));
    }

    private void EnsureIndexes(IProgress<BootstrapProgress>? progress, CancellationToken ct)
    {
        using var db = Open();
        // PRAGMA journal_mode devolve uma linha; tem de ser lido como scalar.
        // Com Execute() o sqlite-net lança "not an error" e os índices nunca
        // chegavam a ser criados.
        db.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
        db.Execute("PRAGMA synchronous=NORMAL");
        db.Execute("PRAGMA temp_store=MEMORY");
        db.Execute("PRAGMA cache_size=-120000");

        var ready = db.QueryScalars<string>(
            "SELECT value FROM meta WHERE key = 'indexes_ready' LIMIT 1").FirstOrDefault();
        if (ready == "1") return;

        var indexes = new (string Name, string Sql)[]
        {
            ("stop_times_stop_dep", "CREATE INDEX IF NOT EXISTS idx_stop_times_stop_dep ON stop_times(stop_id, dep)"),
            ("stop_times_trip_seq", "CREATE INDEX IF NOT EXISTS idx_stop_times_trip_seq ON stop_times(trip_id, seq)"),
            ("trips_route", "CREATE INDEX IF NOT EXISTS idx_trips_route ON trips(route_id)"),
            ("trips_service", "CREATE INDEX IF NOT EXISTS idx_trips_service ON trips(service_id)"),
            ("trips_shape", "CREATE INDEX IF NOT EXISTS idx_trips_shape ON trips(shape_id)"),
            ("calendar_dates", "CREATE INDEX IF NOT EXISTS idx_calendar_dates_date ON calendar_dates(date, service_id)"),
            ("shapes", "CREATE INDEX IF NOT EXISTS idx_shapes_shape_seq ON shapes(shape_id, seq)"),
            ("stops", "CREATE INDEX IF NOT EXISTS idx_stops_stop_id ON stops(stop_id)"),
            ("routes", "CREATE INDEX IF NOT EXISTS idx_routes_short ON routes(short_name)"),
        };

        for (var i = 0; i < indexes.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BootstrapProgress(
                "A criar índices locais…", 0.45 + (0.45 * i / indexes.Length)));
            db.Execute(indexes[i].Sql);
        }

        db.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('indexes_ready', '1')");
        progress?.Report(new BootstrapProgress("Índices prontos.", 0.95));
    }

    private void LoadCaches()
    {
        using var db = Open();

        var feedMeta = db.Query<MetaRow>("SELECT key, value FROM meta")
            .ToDictionary(r => r.Key ?? "", r => r.Value ?? "", StringComparer.Ordinal);
        FeedVersion = feedMeta.GetValueOrDefault("feed_version", "");
        FeedStartDate = feedMeta.GetValueOrDefault("feed_start_date", "");
        FeedEndDate = feedMeta.GetValueOrDefault("feed_end_date", "");

        _routeNumbersByRouteId = new Dictionary<string, string>(StringComparer.Ordinal);
        var stopRows = db.Query<StopRow>(
            "SELECT id, stop_id, code, name, lat, lon FROM stops WHERE lat != 0 AND lon != 0 ORDER BY name");
        _stops = [];
        _stopsByKey = new Dictionary<string, LocalStop>(StringComparer.Ordinal);
        _stopsById = new Dictionary<int, LocalStop>();
        foreach (var row in stopRows)
        {
            var key = row.StopKey ?? "";
            if (string.IsNullOrWhiteSpace(key)) continue;
            var name = string.IsNullOrWhiteSpace(row.Name) ? key : row.Name!;
            var stop = new LocalStop
            {
                Id = row.Id,
                StopId = key,
                Number = string.IsNullOrWhiteSpace(row.Code) ? key : row.Code!,
                Name = name,
                Lat = row.Lat,
                Lng = row.Lng,
                Normalized = Normalize(name),
                Tokens = SearchTokens(name),
            };
            _stops.Add(stop);
            _stopsByKey[key] = stop;
            _stopsById[row.Id] = stop;
        }

        _routesByNumber = new Dictionary<string, LocalRouteInfo>(StringComparer.OrdinalIgnoreCase);
        _routeNumbersByInt = new Dictionary<int, string>();
        _variantIdsByRoute = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var routeRows = db.Query<RouteSummaryRow>(
            """
            SELECT r.id AS route_int, r.route_id, r.short_name, r.long_name, r.color,
                   COUNT(t.id) AS trips,
                   GROUP_CONCAT(DISTINCT t.direction) AS directions
            FROM routes r
            LEFT JOIN trips t ON t.route_id = r.id
            GROUP BY r.id
            """);

        var grouped = new Dictionary<string, List<(RouteSummaryRow Row, string Direction)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in routeRows)
        {
            var routeKey = row.RouteKey ?? "";
            var number = (string.IsNullOrWhiteSpace(row.ShortName) ? routeKey : row.ShortName!).Trim();
            if (string.IsNullOrWhiteSpace(number)) continue;
            _routeNumbersByRouteId[routeKey] = number;
            _routeNumbersByInt[row.RouteInt] = number;
            var direction = DirectionLabel(row.Directions);
            if (!grouped.TryGetValue(number, out var list))
                grouped[number] = list = [];
            list.Add((row, direction));
        }

        foreach (var (number, rows) in grouped)
        {
            var best = rows.OrderByDescending(r => r.Row.Trips).First();
            var info = new LocalRouteInfo
            {
                Number = number,
                Name = string.IsNullOrWhiteSpace(best.Row.LongName) ? number : best.Row.LongName!,
                Direction = best.Direction,
                Color = best.Row.Color ?? "",
                RouteKey = best.Row.RouteKey ?? "",
            };
            foreach (var (row, direction) in rows.OrderByDescending(r => r.Row.Trips))
            {
                info.Variants.Add(new LocalRouteVariant
                {
                    RouteId = row.RouteInt,
                    RouteKey = row.RouteKey ?? "",
                    Headsign = string.IsNullOrWhiteSpace(row.LongName) ? number : row.LongName!,
                    Direction = direction,
                    Trips = row.Trips,
                });
            }
            _routesByNumber[number] = info;
            _variantIdsByRoute[number] = info.Variants.Select(v => v.RouteKey).ToList();
        }

        _sortedRoutes = _routesByNumber.Values
            .OrderBy(r => r.Number, RouteNumberComparer.Instance)
            .ToList();
    }

    private SQLiteConnection Open()
        => new(_dbPath, SQLiteOpenFlags.ReadWrite);

    private static string DirectionLabel(string? directions)
    {
        var set = (directions ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (set.SetEquals(new[] { "0" })) return "Ida";
        if (set.SetEquals(new[] { "1" })) return "Volta";
        return "Ida/Volta";
    }

    private static string Normalize(string value)
    {
        var decomposed = (value ?? "").Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        }
        return string.Join(' ', builder.ToString().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly HashSet<string> StopWords =
    [
        "do", "da", "de", "dos", "das", "e", "a", "o", "os", "as", "em", "na", "no"
    ];

    private static IReadOnlySet<string> SearchTokens(string value)
    {
        var normalized = Normalize(value).Replace('-', ' ');
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !StopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);
    }

    internal static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371000.0;
        var dLat = Math.PI / 180.0 * (lat2 - lat1);
        var dLon = Math.PI / 180.0 * (lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Math.PI / 180.0 * lat1)
                * Math.Cos(Math.PI / 180.0 * lat2)
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * radius * Math.Asin(Math.Sqrt(a));
    }

    internal static DateTimeOffset NowLisbon()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
    }

    // O GTFS traz destinos vazios em algumas viagens. O operador `??` não
    // apanha esses casos (string vazia não é null), por isso usamos esta
    // seleção explícita para nunca mostrar um destino em branco.
    internal static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private sealed class MetaRow
    {
        [Column("key")] public string? Key { get; set; }
        [Column("value")] public string? Value { get; set; }
    }

    private sealed class RouteNumberComparer : IComparer<string>
    {
        public static readonly RouteNumberComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = x ?? "";
            var b = y ?? "";
            var numericA = int.TryParse(a, out var na);
            var numericB = int.TryParse(b, out var nb);
            if (numericA && numericB) return na.CompareTo(nb);
            if (numericA) return -1;
            if (numericB) return 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
