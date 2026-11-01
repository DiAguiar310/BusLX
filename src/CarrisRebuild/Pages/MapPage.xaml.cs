using System.Text.Json;
using CarrisRebuild.Models;
using CarrisRebuild.Services;

namespace CarrisRebuild.Pages;

// Mapa Leaflet/OpenStreetMap dentro do APK. Os dados estáticos vêm da base
// GTFS local e as posições são injetadas a partir do feed GTFS-RT da Carris.
[QueryProperty(nameof(Route), "route")]
[QueryProperty(nameof(Direction), "direction")]
[QueryProperty(nameof(VehicleId), "vehicleId")]
[QueryProperty(nameof(StopId), "stopId")]
[QueryProperty(nameof(StopName), "stopName")]
[QueryProperty(nameof(Towards), "towards")]
public partial class MapPage : ContentPage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LocalGtfsStore _store;
    private readonly GtfsRealtimeService _realtime;
    private string? _loadedUrl;
    private CancellationTokenSource? _pollCts;
    private bool _localLoaded;
    private BusStopDto? _focusStop;
    private readonly Dictionary<string, IReadOnlyList<double[]>> _towardsShapeCache =
        new(StringComparer.Ordinal);
    private string _towardsShapeKey = "";
    private long _lastPushedFeedTimestamp = -1;
    private string _lastPushedFilterKey = "";
    private int _lastPushedVehicleCount = -1;
    private bool _offlineShown;

    public string Route { get; set; } = "";
    public string Direction { get; set; } = "";
    public string VehicleId { get; set; } = "";
    public string StopId { get; set; } = "";
    public string StopName { get; set; } = "";
    public string Towards { get; set; } = "";
    private bool IsTowardsStop => Towards == "1" && !string.IsNullOrWhiteSpace(StopId);

    public MapPage(LocalGtfsStore store, GtfsRealtimeService realtime)
    {
        InitializeComponent();
        _store = store;
        _realtime = realtime;

#if ANDROID
        Browser.HandlerChanged += OnBrowserHandlerChanged;
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = IsTowardsStop
            ? $"A caminho · {StopName}"
            : !string.IsNullOrWhiteSpace(VehicleId)
                ? $"Autocarro {VehicleId}"
                : string.IsNullOrWhiteSpace(Route)
                    ? "Mapa"
                    : string.IsNullOrWhiteSpace(Direction) ? $"Mapa · {Route}" : $"Mapa · {Route} · {Direction}";

        if (_loadedUrl is null)
            LoadMap();
        else
        {
            StartPolling();
            try { await Browser.EvaluateJavaScriptAsync("window.vialxResume && window.vialxResume();"); }
            catch { /* WebView ainda a iniciar. */ }
        }
    }

    protected override async void OnDisappearing()
    {
        StopPolling();
        try { await Browser.EvaluateJavaScriptAsync("window.vialxPause && window.vialxPause();"); }
        catch { /* WebView já não existe. */ }
        base.OnDisappearing();
    }

    private void LoadMap()
    {
#if ANDROID
        var baseUrl = "file:///android_asset/map.html";
#else
        var baseUrl = "map.html";
#endif

        var url = $"{baseUrl}?local=1";
        if (!string.IsNullOrWhiteSpace(Route))
            url += $"&route={Uri.EscapeDataString(Route)}";
        if (!string.IsNullOrWhiteSpace(Direction))
            url += $"&direction={Uri.EscapeDataString(Direction)}";
        if (!string.IsNullOrWhiteSpace(VehicleId))
            url += $"&vehicleId={Uri.EscapeDataString(VehicleId)}";
        if (IsTowardsStop)
        {
            url += $"&stopId={Uri.EscapeDataString(StopId)}";
            url += $"&stopName={Uri.EscapeDataString(StopName)}";
            url += "&towards=1";
        }

        _loadedUrl = url;
        LoadingOverlay.IsVisible = true;
        Browser.Source = url;
    }

    private async Task InitializeLocalAsync()
    {
        try
        {
            LoadingOverlay.IsVisible = true;
            await _store.EnsureInitializedAsync();

            var stops = await _store.GetBusStopsAsync();
            var stopRows = stops
                .Select(s => new object[]
                {
                    s.Id,
                    s.Number,
                    s.Name,
                    Math.Round(s.Lat, 6),
                    Math.Round(s.Lng, 6),
                })
                .ToList();
            await EvaluateAsync(
                $"window.vialxSetStops({JsonSerializer.Serialize(stopRows, JsonOptions)});");

            if (!string.IsNullOrWhiteSpace(Route))
            {
                var route = await _store.GetRouteAsync(Route);
                var colors = RouteColors.Get(Route);
                var ida = await _store.GetShapeAsync(Route, "Ida");
                var volta = await _store.GetShapeAsync(Route, "Volta");
                var all = await _store.GetShapeAsync(Route, "");
                var shapes = new Dictionary<string, IReadOnlyList<double[]>>
                {
                    ["Ida"] = ida.Points,
                    ["Volta"] = volta.Points,
                    ["all"] = all.Points,
                };
                var routeInfo = new
                {
                    route.Number,
                    route.Variants,
                    route.Directions,
                    badgeColor = colors.Badge,
                    lineColor = colors.Line,
                    textColor = colors.Text,
                };
                await EvaluateAsync(
                    $"window.vialxSetRoute({JsonSerializer.Serialize(routeInfo, JsonOptions)}," +
                    $"{JsonSerializer.Serialize(shapes, JsonOptions)});");
            }

            if (IsTowardsStop)
            {
                _focusStop = await _store.GetStopAsync(StopId);
                if (_focusStop is not null)
                {
                    await EvaluateAsync(
                        $"window.vialxFocusStop({JsonSerializer.Serialize(_focusStop.Lat, JsonOptions)}," +
                        $"{JsonSerializer.Serialize(_focusStop.Lng, JsonOptions)}," +
                        $"{JsonSerializer.Serialize(_focusStop.Name, JsonOptions)});");
                }
            }

            _localLoaded = true;
            LoadingOverlay.IsVisible = false;
            _lastPushedFeedTimestamp = -1;
            _lastPushedFilterKey = "";
            _lastPushedVehicleCount = -1;
            _offlineShown = false;
            await PushVehiclesAsync(force: true);
            StartPolling();
        }
        catch (Exception ex)
        {
            LoadingOverlay.IsVisible = false;
            await DisplayAlert("Mapa", "Não foi possível carregar os dados locais: " + ex.Message, "OK");
        }
    }

    private async Task UpdateTowardsRouteShapesAsync(IReadOnlyList<VehicleSnapshot> vehicles)
    {
        var pairs = vehicles
            .Where(v => !string.IsNullOrWhiteSpace(v.RouteNumber))
            .Select(v => (
                Route: v.RouteNumber.Trim(),
                Direction: string.IsNullOrWhiteSpace(v.Direction) ? "" : v.Direction.Trim()))
            .Distinct()
            .OrderBy(p => p.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Direction, StringComparer.Ordinal)
            .ToList();

        var key = string.Join("|", pairs.Select(p => $"{p.Route}:{p.Direction}"));
        if (key == _towardsShapeKey) return;
        _towardsShapeKey = key;

        if (pairs.Count == 0)
        {
            await EvaluateAsync("window.vialxSetRouteShapes([]);");
            return;
        }

        var shapes = new List<object>(pairs.Count);
        foreach (var pair in pairs)
        {
            var cacheKey = $"{pair.Route}|{pair.Direction}";
            if (!_towardsShapeCache.TryGetValue(cacheKey, out var points))
            {
                var shape = await _store.GetShapeAsync(pair.Route, pair.Direction);
                points = shape.Points;
                if (points.Count == 0 && pair.Direction.Length > 0)
                    points = (await _store.GetShapeAsync(pair.Route, "")).Points;
                _towardsShapeCache[cacheKey] = points;
            }

            if (points.Count > 1)
            {
                var colors = RouteColors.Get(pair.Route);
                shapes.Add(new
                {
                    routeNumber = pair.Route,
                    direction = pair.Direction,
                    points,
                    badgeColor = colors.Badge,
                    lineColor = colors.Line,
                    textColor = colors.Text,
                });
            }
        }

        await EvaluateAsync(
            $"window.vialxSetRouteShapes({JsonSerializer.Serialize(shapes, JsonOptions)});");
    }

    private async Task PushVehiclesAsync(bool force = false)
    {
        try
        {
            var vehicles = await _realtime.GetVehiclesAsync(
                string.IsNullOrWhiteSpace(Route) ? null : Route,
                refreshIfStale: false);

            if (!string.IsNullOrWhiteSpace(VehicleId))
            {
                vehicles = vehicles
                    .Where(v => string.Equals(v.VehicleId, VehicleId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else if (IsTowardsStop)
            {
                vehicles = await _store.GetVehiclesTowardsStopAsync(StopId, vehicles);
            }

            if (IsTowardsStop)
                await UpdateTowardsRouteShapesAsync(vehicles);

            var feedTimestamp = _realtime.FeedTimestamp;
            var filterKey = $"{Route}|{Direction}|{VehicleId}|{StopId}|{Towards}";
            var hasError = !string.IsNullOrWhiteSpace(_realtime.Error);

            // O feed só muda a cada ~5 s. Não vale a pena voltar a serializar
            // e injetar o mesmo conjunto de veículos em cada ciclo.
            if (!force
                && feedTimestamp > 0
                && feedTimestamp == _lastPushedFeedTimestamp
                && filterKey == _lastPushedFilterKey
                && vehicles.Count == _lastPushedVehicleCount)
            {
                if (vehicles.Count == 0 && hasError && !_offlineShown)
                {
                    _offlineShown = true;
                    await EvaluateAsync(
                        $"window.vialxShowOffline({JsonSerializer.Serialize("Sem ligação à Carris. A tentar novamente…", JsonOptions)});");
                }
                return;
            }

            // Payload compacto: as cores são enviadas uma vez por carreira e
            // cada veículo é apenas [id, carreira, lat, lng, sentido, hora].
            var routeMeta = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var rows = new List<object[]>(vehicles.Count);
            foreach (var v in vehicles)
            {
                var route = (v.RouteNumber ?? "").Trim();
                if (route.Length > 0 && !routeMeta.ContainsKey(route))
                {
                    routeMeta[route] =
                    [
                        RouteColors.BadgeColor(route),
                        RouteColors.LineColor(route),
                        RouteColors.TextColor(route),
                    ];
                }

                var id = v.VehicleId;
                if (string.IsNullOrWhiteSpace(id)) id = v.TripId;
                if (string.IsNullOrWhiteSpace(id)) id = $"{v.Lat:F6},{v.Lng:F6}";
                rows.Add(
                [
                    id,
                    route,
                    Math.Round(v.Lat, 6),
                    Math.Round(v.Lng, 6),
                    v.Direction ?? "",
                    v.Timestamp,
                ]);
            }

            var payload = new { t = feedTimestamp, r = routeMeta, v = rows };
            var pushed = await EvaluateAsync(
                $"window.vialxSetVehicles({JsonSerializer.Serialize(payload, JsonOptions)});");
            if (pushed)
            {
                _lastPushedFeedTimestamp = feedTimestamp;
                _lastPushedFilterKey = filterKey;
                _lastPushedVehicleCount = vehicles.Count;
                _offlineShown = false;
            }

            if (hasError && vehicles.Count == 0)
            {
                _offlineShown = true;
                await EvaluateAsync(
                    $"window.vialxShowOffline({JsonSerializer.Serialize("Sem ligação à Carris. A tentar novamente…", JsonOptions)});");
            }
        }
        catch (Exception ex)
        {
            try
            {
                await EvaluateAsync(
                    $"window.vialxShowOffline({JsonSerializer.Serialize(ex.Message, JsonOptions)});");
            }
            catch { /* WebView fechado. */ }
        }
    }

    private Task<bool> EvaluateAsync(string script)
        => MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                await Browser.EvaluateJavaScriptAsync(script);
                return true;
            }
            catch
            {
                // Navegação a decorrer ou WebView já fechado.
                return false;
            }
        });

    private void StartPolling()
    {
        if (!_localLoaded || _pollCts is not null) return;
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                    await PushVehiclesAsync();
            }
            catch (OperationCanceledException) { }
            catch { /* mantém a última posição */ }
        }, token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (_loadedUrl is null) return;
        LoadingOverlay.IsVisible = true;
    }

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
        {
            LoadingOverlay.IsVisible = false;
            DisplayAlert("Mapa", "Não foi possível carregar o mapa. Verifica a ligação à Internet.", "OK");
            return;
        }

        _ = InitializeLocalAsync();
    }

#if ANDROID
    private void OnBrowserHandlerChanged(object? sender, EventArgs e)
    {
        if (Browser.Handler?.PlatformView is not Android.Webkit.WebView native) return;

#if DEBUG
        // Permite inspecionar o WebView do mapa durante o desenvolvimento.
        Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true);
#endif
        var settings = native.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.AllowFileAccess = true;
        settings.BuiltInZoomControls = false;
        settings.DisplayZoomControls = false;
        settings.MixedContentMode = Android.Webkit.MixedContentHandling.AlwaysAllow;
    }
#endif
}
