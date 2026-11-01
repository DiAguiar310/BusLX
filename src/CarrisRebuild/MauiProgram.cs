using CarrisRebuild.Pages;
using CarrisRebuild.Services;

namespace CarrisRebuild;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        SQLitePCL.Batteries_V2.Init();
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Tudo é local: base GTFS embutida + feed GTFS-RT público da Carris.
        builder.Services.AddSingleton<LocalGtfsStore>();
        builder.Services.AddSingleton<GtfsRealtimeService>();
        builder.Services.AddSingleton<ICarrisApi, LocalCarrisApi>();
        builder.Services.AddSingleton<FavoritesService>(_ =>
            new FavoritesService(Path.Combine(FileSystem.AppDataDirectory, "favs.db3")));
        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddSingleton<BootstrapPage>();
        builder.Services.AddTransient<SplashPage>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<StopsPage>();
        builder.Services.AddTransient<StopDetailPage>();
        builder.Services.AddTransient<RoutesPage>();
        builder.Services.AddTransient<RouteDetailPage>();
        builder.Services.AddTransient<StopTimesPage>();
        builder.Services.AddTransient<MapPage>();
        builder.Services.AddTransient<AboutPage>();
        return builder.Build();
    }
}
