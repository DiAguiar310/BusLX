using CarrisRebuild.Models;
using CarrisRebuild.Services;

namespace CarrisRebuild.Pages;

public partial class HomePage : ContentPage
{
    private readonly ICarrisApi _api;
    private readonly FavoritesService _favs;
    private bool _loading;

    public HomePage(ICarrisApi api, FavoritesService favs)
    {
        InitializeComponent();
        _api = api;
        _favs = favs;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loading) return;
        _loading = true;
        try
        {
            await _favs.InitAsync();

            var stopsTask = _favs.AllStopsAsync();
            var routesTask = _favs.AllRoutesAsync();
            var networkStopsTask = _api.GetBusStopsAsync();
            var networkRoutesTask = _api.GetRoutesAsync();

            await Task.WhenAll(stopsTask, routesTask, networkStopsTask, networkRoutesTask);

            StopCountLabel.Text = $"{networkStopsTask.Result.Count}";
            RouteCountLabel.Text = $"{networkRoutesTask.Result.Count}";
            BindableLayout.SetItemsSource(FavRouteList, routesTask.Result);

            // Mostra já os nomes das paragens guardadas; as chegadas entram
            // assim que a base local responder, sem um falso estado vazio.
            var favoriteStops = stopsTask.Result
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            BindableLayout.SetItemsSource(FavList, favoriteStops
                .Select(s => new FavoriteStopCard(s.Id, s.Name, [], Loading: true))
                .ToList());

            var cards = new List<FavoriteStopCard>();
            foreach (var stop in favoriteStops)
            {
                IReadOnlyList<EstimationDto> next = [];
                try
                {
                    next = (await _api.GetEstimationsAsync(stop.Id))
                        .Take(2)
                        .ToList();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Home favorito {stop.Id}: {ex}");
                }
                cards.Add(new FavoriteStopCard(stop.Id, stop.Name, next));
            }

            BindableLayout.SetItemsSource(FavList, cards);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Home: {ex}");
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnStops(object? s, EventArgs e)
        => await Shell.Current.GoToAsync("//stops");

    private async void OnRoutes(object? s, EventArgs e)
        => await Shell.Current.GoToAsync("//routes");

    private async void OnMap(object? s, EventArgs e)
        => await Shell.Current.GoToAsync("//map");

    private async void OnFavStop(object? s, TappedEventArgs e)
    {
        if ((s as Element)?.BindingContext is FavoriteStopCard stop)
            await Shell.Current.GoToAsync(
                $"{nameof(StopDetailPage)}?stopId={Uri.EscapeDataString(stop.Id)}&name={Uri.EscapeDataString(stop.Name)}");
    }

    private async void OnFavRoute(object? s, TappedEventArgs e)
    {
        if ((s as Element)?.BindingContext is FavoriteRoute route)
            await Shell.Current.GoToAsync(
                $"{nameof(RouteDetailPage)}?number={Uri.EscapeDataString(route.Number)}&name={Uri.EscapeDataString(route.Name)}");
    }
}
