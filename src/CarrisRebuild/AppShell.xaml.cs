using CarrisRebuild.Pages;

namespace CarrisRebuild;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(StopDetailPage), typeof(StopDetailPage));
        Routing.RegisterRoute(nameof(RouteDetailPage), typeof(RouteDetailPage));
        Routing.RegisterRoute(nameof(StopTimesPage), typeof(StopTimesPage));
    }
}
