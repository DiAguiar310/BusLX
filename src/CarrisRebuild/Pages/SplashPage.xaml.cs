namespace CarrisRebuild.Pages;

public partial class SplashPage : ContentPage
{
    public SplashPage()
    {
        InitializeComponent();
        VersionLabel.Text = $"Versão 1.1 · {DateTime.Now:yyyy}";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!Preferences.Get("terms_accepted", false))
        {
            var ok = await DisplayAlert("Termos", "Aceitas os termos de utilização do rebuild comunitário?", "Aceitar", "Recusar");
            if (!ok)
            {
                System.Environment.Exit(0);
                return;
            }
            Preferences.Set("terms_accepted", true);
        }
        await Task.Delay(800);
        await Shell.Current.GoToAsync("//home");
    }
}
