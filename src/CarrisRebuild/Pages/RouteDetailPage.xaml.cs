using CarrisRebuild.Models;
using CarrisRebuild.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CarrisRebuild.Pages;

[QueryProperty(nameof(Number), "number")]
[QueryProperty(nameof(RouteName), "name")]
public partial class RouteDetailPage : ContentPage
{
    private readonly ICarrisApi _api;
    private readonly FavoritesService _favs;
    private readonly IServiceProvider _services;
    private IReadOnlyList<RouteDirectionDto> _directions = [];
    public string Number { get; set; } = "";
    public string RouteName { get; set; } = "";

    public RouteDetailPage(ICarrisApi api, FavoritesService favs, IServiceProvider services)
    {
        InitializeComponent();
        _api = api;
        _favs = favs;
        _services = services;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = $"Carreira {Number}";
        var routeColor = RouteColors.Get(Number);
        NumberBadge.BackgroundColor = Color.FromArgb(routeColor.Badge);
        NumberBadge.BorderColor = Color.FromArgb(routeColor.Badge);
        NumberLabel.TextColor = Color.FromArgb(routeColor.Text);
        NumberLabel.Text = Number;
        Header.Text = string.IsNullOrWhiteSpace(RouteName) ? $"Carreira {Number}" : RouteName;
        RouteNameLabel.Text = "Rede Carris · Lisboa";
        FavButton.Text = "…";

        try
        {
            await _favs.InitAsync();
            FavButton.Text = await _favs.IsRouteFavAsync(Number) ? "★ Guardada" : "☆ Guardar carreira";

            var full = await _api.GetRouteAsync(Number);
            Header.Text = string.IsNullOrWhiteSpace(RouteName) ? $"Carreira {full.Number}" : RouteName;
            _directions = full.Directions ?? [];
            ConfigureDirectionButtons();
            BindableLayout.SetItemsSource(Variants, full.Variants);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Route detail: {ex}");
        }
    }

    private async void OnFav(object? s, EventArgs e)
    {
        var saved = await _favs.ToggleRouteAsync(Number, RouteName);
        FavButton.Text = saved ? "★ Guardada" : "☆ Guardar carreira";
    }

    private async void OnMap(object? s, EventArgs e) => await OpenMapAsync();

    private async void OnMapIda(object? s, EventArgs e) => await OpenMapAsync("Ida");

    private async void OnMapVolta(object? s, EventArgs e) => await OpenMapAsync("Volta");

    private async Task OpenMapAsync(string? direction = null)
    {
        var map = _services.GetRequiredService<MapPage>();
        map.Route = Number;
        map.Direction = direction ?? "";
        await Shell.Current.Navigation.PushAsync(map);
    }

    private RouteDirectionDto? FindDirection(string code) =>
        _directions.FirstOrDefault(d => string.Equals(d.Direction, code, StringComparison.OrdinalIgnoreCase));

    private string DirectionOption(string code)
    {
        var d = FindDirection(code);
        if (d is null) return code;
        return d.Circular ? d.Label : $"{d.Direction} → {d.ShortLabel}";
    }

    private void ConfigureDirectionButtons()
    {
        if (_directions.Count == 0) return;

        var ida = FindDirection("Ida");
        var volta = FindDirection("Volta");
        IdaButton.IsVisible = ida is not null;
        VoltaButton.IsVisible = volta is not null;

        // Com um único sentido, o botão ocupa a largura toda em vez de
        // deixar uma coluna vazia ao lado.
        var onlyIda = ida is not null && volta is null;
        var onlyVolta = ida is null && volta is not null;
        Grid.SetColumn(IdaButton, 0);
        Grid.SetColumnSpan(IdaButton, onlyIda ? 2 : 1);
        Grid.SetColumn(VoltaButton, onlyVolta ? 0 : 1);
        Grid.SetColumnSpan(VoltaButton, onlyVolta ? 2 : 1);

        if (ida is not null)
            IdaButton.Text = ida.Circular ? ida.Label : $"{ida.Direction} → {ida.ShortLabel}";
        if (volta is not null)
            VoltaButton.Text = volta.Circular ? volta.Label : $"{volta.Direction} → {volta.ShortLabel}";
    }

    private async void OnVariantTap(object? s, TappedEventArgs e)
    {
        if ((s as Element)?.BindingContext is not VariantDto variant) return;

        var options = new List<(string Code, string Text)>();
        if (_directions.Count == 0)
        {
            options.Add(("Ida", "Ida"));
            options.Add(("Volta", "Volta"));
        }
        else
        {
            foreach (var d in _directions)
                options.Add((d.Direction, DirectionOption(d.Direction)));
        }

        var choice = await DisplayActionSheet(
            $"Sentido · {variant.Headsign}", "Cancelar", null, options.Select(o => o.Text).ToArray());
        var selected = options.FirstOrDefault(o => o.Text == choice);
        if (string.IsNullOrEmpty(selected.Code)) return;

        await OpenMapAsync(selected.Code);
    }
}
