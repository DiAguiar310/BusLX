using CarrisRebuild.Models;
using CarrisRebuild.Services;

namespace CarrisRebuild.Pages;

public partial class StopsPage : ContentPage
{
    private readonly ICarrisApi _api;
    private List<BusStopDto> _all = [];

    public StopsPage(ICarrisApi api)
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
            _all = [.. await _api.GetBusStopsAsync()];
            ApplyFilter("");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Stops: {ex}");
        }
    }

    private void OnSearch(object? s, TextChangedEventArgs e) => ApplyFilter(e.NewTextValue);

    private void ApplyFilter(string? text)
    {
        var q = text?.Trim() ?? "";
        var res = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || x.Number.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        List.ItemsSource = res;
        CountLabel.Text = q.Length == 0
            ? $"{res.Count} paragens na rede"
            : res.Count == 1 ? "1 resultado" : $"{res.Count} resultados";
    }

    private void OnSearchPressed(object? s, EventArgs e) => ApplyFilter(Search.Text);

    private async void OnStopTap(object? s, TappedEventArgs e)
    {
        if (e.Parameter is not BusStopDto stop) return;
        await Search.HideSoftInputAsync(CancellationToken.None);
        Search.Unfocus();
        await Shell.Current.GoToAsync(
            $"{nameof(StopDetailPage)}?stopId={Uri.EscapeDataString(stop.Id)}&name={Uri.EscapeDataString(stop.Name)}");
    }
}
