using CarrisRebuild.Models;

namespace CarrisRebuild.Services;

// Vai diretamente ao feed público GTFS-RT da Carris. Não passa por servidor
// intermédio nem precisa de chave/autenticação.
public sealed class GtfsRealtimeService
{
    public const string FeedUrl =
        "https://gateway.carris.pt/gateway/gtfs/api/v2.10/GTFS/realtime/vehiclepositions";
    public const string UserAgent = "BusLX/1.1 (projeto independente; GTFS público Carris)";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly LocalGtfsStore _store;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _sync = new();
    private RealtimeFeed _feed = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastSuccess;
    private string? _error;

    public GtfsRealtimeService(LocalGtfsStore store)
    {
        _store = store;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public string? Error
    {
        get { lock (_sync) return _error; }
    }

    public long LastSuccess
    {
        get { lock (_sync) return _lastSuccess; }
    }

    public long FeedTimestamp
    {
        get { lock (_sync) return _feed.FeedTimestamp; }
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async Task<RealtimeFeed> GetFeedAsync(bool refreshIfStale = true, CancellationToken ct = default)
    {
        if (refreshIfStale && UnixNow() - LastSuccess >= 4)
            await RefreshAsync(ct);
        lock (_sync)
            return _feed;
    }

    public async Task<IReadOnlyList<VehicleSnapshot>> GetVehiclesAsync(
        string? routeNumber = null,
        bool refreshIfStale = true,
        CancellationToken ct = default)
    {
        var feed = await GetFeedAsync(refreshIfStale, ct);
        if (string.IsNullOrWhiteSpace(routeNumber))
            return feed.Vehicles;
        return feed.Vehicles
            .Where(v => string.Equals(v.RouteNumber, routeNumber, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task EnsureFreshAsync(CancellationToken ct = default)
    {
        if (UnixNow() - LastSuccess >= 4)
            await RefreshAsync(ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try { await RefreshAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { SetError($"{ex.GetType().Name}: {ex.Message}"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!_store.IsReady) return;
        await _refreshGate.WaitAsync(ct);
        try
        {
            var payload = await _http.GetByteArrayAsync(FeedUrl, ct);
            var feed = GtfsRealtimeParser.Parse(
                payload,
                _store.RouteNumbersByRouteId,
                UnixNow());
            lock (_sync)
            {
                _feed = feed;
                _lastSuccess = UnixNow();
                _error = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetError($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void SetError(string message)
    {
        lock (_sync) _error = message;
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
