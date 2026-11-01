using CarrisRebuild.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CarrisRebuild;

public partial class App : Application
{
    public App(IServiceProvider services)
    {
        InitializeComponent();
        // O arranque prepara a base GTFS local e só depois abre o AppShell.
        MainPage = services.GetRequiredService<BootstrapPage>();
    }
}
