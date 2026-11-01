using Microsoft.Extensions.DependencyInjection;
using CarrisRebuild.Services;

namespace CarrisRebuild.Pages;

// Primeira página: instala a base GTFS embutida e arranca o feed GTFS-RT
// direto da Carris. Não existe backend intermédio.
public sealed class BootstrapPage : ContentPage
{
    private static readonly Color BrandBlue = Color.FromArgb("#00569F");
    private static readonly Color BrandYellow = Color.FromArgb("#FFCC00");
    private static readonly Color BrandInk = Color.FromArgb("#1D1D1B");
    private static readonly Color OnBlueMuted = Color.FromArgb("#D6E7F5");

    private readonly LocalGtfsStore _store;
    private readonly GtfsRealtimeService _realtime;
    private readonly IServiceProvider _services;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly ActivityIndicator _spinner;
    private readonly Button _retry;
    private bool _running;

    public BootstrapPage(
        LocalGtfsStore store,
        GtfsRealtimeService realtime,
        IServiceProvider services)
    {
        _store = store;
        _realtime = realtime;
        _services = services;
        Title = "BusLX";
        BackgroundColor = BrandBlue;

        var logo = new Image
        {
            Source = ImageSource.FromFile("brand_mark.png"),
            WidthRequest = 84,
            HeightRequest = 84,
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Center,
        };
        var logoCard = new Frame
        {
            BackgroundColor = Colors.White,
            BorderColor = Colors.White,
            CornerRadius = 22,
            HasShadow = false,
            Padding = 8,
            HorizontalOptions = LayoutOptions.Center,
            Content = logo,
        };

        _status = new Label
        {
            Text = "A iniciar…",
            TextColor = Colors.White,
            FontSize = 15,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        _progress = new ProgressBar
        {
            ProgressColor = BrandYellow,
            BackgroundColor = Color.FromArgb("#2B6FA8"),
            HeightRequest = 6,
        };
        _spinner = new ActivityIndicator
        {
            IsRunning = true,
            Color = BrandYellow,
            HorizontalOptions = LayoutOptions.Center,
        };
        _retry = new Button
        {
            Text = "Tentar novamente",
            IsVisible = false,
            BackgroundColor = BrandYellow,
            TextColor = BrandInk,
            CornerRadius = 13,
            HeightRequest = 48,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Fill,
        };
        _retry.Clicked += async (_, _) => await RunAsync();

        var main = new VerticalStackLayout
        {
            Spacing = 10,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                logoCard,
                new Label
                {
                    Text = "BusLX",
                    TextColor = Colors.White,
                    FontSize = 38,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0),
                },
                new Label
                {
                    Text = "Lisboa em movimento",
                    TextColor = OnBlueMuted,
                    FontSize = 14,
                    HorizontalTextAlignment = TextAlignment.Center,
                },
                new Label
                {
                    Text = "DADOS PÚBLICOS DA CARRIS",
                    TextColor = BrandYellow,
                    FontSize = 9,
                    FontAttributes = FontAttributes.Bold,
                    CharacterSpacing = 1.2,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0),
                },
                new BoxView { HeightRequest = 18, Color = Colors.Transparent },
                _spinner,
                _status,
                _progress,
                new Label
                {
                    Text = "A base de horários fica no telemóvel. " +
                           "As posições dos autocarros são lidas diretamente da Carris.",
                    TextColor = OnBlueMuted,
                    FontSize = 11,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0),
                },
            },
        };
        var retryArea = new VerticalStackLayout
        {
            Spacing = 12,
            VerticalOptions = LayoutOptions.End,
            Children = { _retry },
        };
        Content = new Grid
        {
            Padding = new Thickness(28, 24),
            Children = { main, retryArea },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync();
    }

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        _retry.IsVisible = false;
        _spinner.IsRunning = true;
        _spinner.IsVisible = true;
        _progress.Progress = 0;

        try
        {
            var progress = new Progress<BootstrapProgress>(p =>
            {
                _status.Text = p.Message;
                _progress.Progress = Math.Clamp(p.Fraction, 0, 1);
            });
            await _store.EnsureInitializedAsync(progress);
            _realtime.Start();
            _status.Text = "Pronto.";
            _progress.Progress = 1;
            await Task.Delay(150);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (Application.Current is not null)
                    Application.Current.MainPage = _services.GetRequiredService<AppShell>();
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Não foi possível preparar os dados.\n" + ex.Message;
            _retry.IsVisible = true;
            _spinner.IsRunning = false;
        }
        finally
        {
            _running = false;
        }
    }
}
