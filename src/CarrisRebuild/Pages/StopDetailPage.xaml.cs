using CarrisRebuild.Models;
using CarrisRebuild.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CarrisRebuild.Pages;

[QueryProperty(nameof(StopId), "stopId")]
[QueryProperty(nameof(StopName), "name")]
public partial class StopDetailPage : ContentPage
{
    private readonly ICarrisApi _api;
    private readonly FavoritesService _favs;
    private readonly IServiceProvider _services;
    public string StopId { get; set; } = "1001";
    public string StopName { get; set; } = "";
    private CancellationTokenSource? _cts;
    private bool _routesLoaded;

    public StopDetailPage(ICarrisApi api, FavoritesService favs, IServiceProvider services)
    {
        InitializeComponent();
        _api = api;
        _favs = favs;
        _services = services;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Title = string.IsNullOrEmpty(StopName) ? "Paragem" : StopName;
        _ = RefreshFavAsync();
        _cts = new CancellationTokenSource();
        _ = PollAsync(_cts.Token);
    }

    protected override void OnDisappearing()
    {
        _cts?.Cancel();
        base.OnDisappearing();
    }

    private async Task RefreshFavAsync()
    {
        try
        {
            await _favs.InitAsync();
            FavButton.Text = await _favs.IsStopFavAsync(StopId) ? "★ Guardada" : "☆ Guardar";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Stop fav: {ex}");
        }
    }

    private async void OnFav(object? s, EventArgs e)
    {
        var saved = await _favs.ToggleStopAsync(StopId, StopName);
        FavButton.Text = saved ? "★ Guardada" : "☆ Guardar";
    }

    private async void OnTowards(object? s, EventArgs e)
    {
        var map = _services.GetRequiredService<MapPage>();
        map.StopId = StopId;
        map.StopName = StopName;
        map.Towards = "1";
        await Shell.Current.Navigation.PushAsync(map);
    }

    private async void OnEstimationTap(object? s, TappedEventArgs e)
    {
        if ((s as Element)?.BindingContext is not EstimationDto estimation) return;

        if (!estimation.CanTrack)
        {
            await DisplayAlert(
                "Ainda sem posição",
                "Esta partida é uma previsão e ainda não tem um autocarro identificado no feed em tempo real. Tenta novamente quando estiver mais perto.",
                "OK");
            return;
        }

        var map = _services.GetRequiredService<MapPage>();
        map.VehicleId = estimation.VehicleId;
        map.Route = estimation.RouteNumber;
        map.Direction = estimation.Direction ?? "";
        map.StopName = StopName;
        await Shell.Current.Navigation.PushAsync(map);
    }

    private async void OnRouteTap(object? s, TappedEventArgs e)
    {
        if ((s as Element)?.BindingContext is RouteDto route)
            await Shell.Current.GoToAsync($"{nameof(RouteDetailPage)}?number={Uri.EscapeDataString(route.Number)}");
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do { await RefreshAsync(ct); }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Stop poll: {ex}");
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var est = await _api.GetEstimationsAsync(StopId, ct);
            if (ct.IsCancellationRequested) return;
            List.ItemsSource = est;

            if (!_routesLoaded)
            {
                RoutesList.ItemsSource = await _api.GetRoutesForBusStopAsync(StopId, ct);
                _routesLoaded = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Stop refresh: {ex}");
        }
    }
}
