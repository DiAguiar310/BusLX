using CarrisRebuild.Models;
using CarrisRebuild.Services;

namespace CarrisRebuild.Pages;

public partial class RoutesPage : ContentPage
{
    private readonly ICarrisApi _api;
    private List<RouteDto> _all = [];

    public RoutesPage(ICarrisApi api)
    {
        InitializeComponent();
        _api = api;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_all.Count > 0) return;

        try
        {
            _all = [.. await _api.GetRoutesAsync()];
            ApplyFilter("");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Routes: {ex}");
        }
    }

    private void OnSearch(object? s, TextChangedEventArgs e) => ApplyFilter(e.NewTextValue);

    private void ApplyFilter(string? text)
    {
        var q = text?.Trim() ?? "";
        var res = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(x => x.Number.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        List.ItemsSource = res;
        CountLabel.Text = res.Count == 1 ? "1 carreira" : $"{res.Count} carreiras";
    }

    private void OnSearchPressed(object? s, EventArgs e) => ApplyFilter(Search.Text);

    private async void OnRouteTap(object? s, TappedEventArgs e)
    {
        if (e.Parameter is not RouteDto route) return;
        await Search.HideSoftInputAsync(CancellationToken.None);
        Search.Unfocus();
        await Shell.Current.GoToAsync(
            $"{nameof(RouteDetailPage)}?number={Uri.EscapeDataString(route.Number)}&name={Uri.EscapeDataString(route.Name)}");
    }
}
