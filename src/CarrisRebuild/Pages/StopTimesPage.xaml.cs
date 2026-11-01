using CarrisRebuild.Services;

namespace CarrisRebuild.Pages;

[QueryProperty(nameof(Route), "route")]
public partial class StopTimesPage : ContentPage
{
    private readonly ICarrisApi _api;
    public string Route { get; set; } = "726";

    public StopTimesPage(ICarrisApi api)
    {
        InitializeComponent();
        _api = api;
    }

    private async void OnLoad(object? s, EventArgs e)
    {
        var date = Day.Date.ToString("yyyy-MM-dd");
        List.ItemsSource = await _api.GetStopTimesAsync(Route, "Ida", "1001", date);
    }
}
