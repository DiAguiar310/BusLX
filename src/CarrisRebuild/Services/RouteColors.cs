using System.Globalization;
using Microsoft.Maui.Graphics;

namespace CarrisRebuild.Services;

/// <summary>
/// Cores oficiais das carreiras da Carris, recolhidas das páginas de carreira
/// (badge) e do diagrama de rede (cores das linhas por zona).
/// </summary>
public static class RouteColors
{
    public const string Yellow = "#FFDC00";
    public const string Magenta = "#EC008C";
    public const string Blue = "#00AEEF";
    public const string Green = "#2FB61E";
    public const string Red = "#ED1C24";
    public const string Orange = "#F7941E";
    public const string Grey = "#8C8C99";
    public const string NightBlue = "#091B7D";
    public const string Fallback = Grey;

    // A cor da linha das carreiras de bairro é a cor da zona onde circulam;
    // o número (badge) é sempre amarelo, como na sinalética da Carris.
    private static readonly Dictionary<string, string> LineColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10B"] = Orange,
        ["12E"] = Orange,
        ["13B"] = Orange,
        ["15E"] = Magenta,
        ["17B"] = Orange,
        ["18E"] = Magenta,
        ["19B"] = Orange,
        ["22B"] = Orange,
        ["24E"] = Orange,
        ["25E"] = Orange,
        ["26B"] = Red,
        ["28E"] = Orange,
        ["29B"] = Red,
        ["31B"] = Red,
        ["32B"] = Red,
        ["34B"] = Red,
        ["37B"] = Orange,
        ["40B"] = Green,
        ["41B"] = Green,
        ["45B"] = Green,
        ["46B"] = Green,
        ["49B"] = Red,
        ["51E"] = Orange,
        ["52B"] = Blue,
        ["53E"] = Orange,
        ["54E"] = Orange,
        ["55B"] = Blue,
        ["55E"] = Orange,
        ["58B"] = Blue,
        ["60B"] = Blue,
        ["61B"] = Blue,
        ["64B"] = Orange,
        ["65B"] = Orange,
        ["67B"] = Orange,
        ["71B"] = Blue,
        ["72B"] = Blue,
        ["73B"] = Magenta,
        ["76B"] = Magenta,
        ["79B"] = Magenta,
        ["97"] = Green, // PMR; serviço especial sem cor publicada, zona Norte.
        ["201"] = NightBlue,
        ["202"] = NightBlue,
        ["203"] = NightBlue,
        ["206"] = NightBlue,
        ["207"] = NightBlue,
        ["208"] = NightBlue,
        ["210"] = NightBlue,
        ["701"] = Grey,
        ["702"] = Orange,
        ["703"] = Grey,
        ["705"] = Red,
        ["706"] = Orange,
        ["708"] = Red,
        ["709"] = Orange,
        ["711"] = Blue,
        ["712"] = Orange,
        ["713"] = Orange,
        ["714"] = Magenta,
        ["716"] = Blue,
        ["717"] = Green,
        ["718"] = Red,
        ["720"] = Grey,
        ["722"] = Red,
        ["723"] = Magenta,
        ["724"] = Blue,
        ["725"] = Red,
        ["726"] = Blue,
        ["727"] = Grey,
        ["728"] = Grey,
        ["729"] = Blue,
        ["730"] = Orange,
        ["731"] = Red,
        ["732"] = Magenta,
        ["734"] = Orange,
        ["735"] = Green,
        ["736"] = Green,
        ["737"] = Orange,
        ["738"] = Grey,
        ["742"] = Grey,
        ["744"] = Red,
        ["746"] = Blue,
        ["747"] = Green,
        ["748"] = Magenta,
        ["749"] = Red,
        ["750"] = Grey,
        ["751"] = Magenta,
        ["753"] = Grey,
        ["754"] = Blue,
        ["755"] = Red,
        ["756"] = Grey,
        ["758"] = Blue,
        ["759"] = Red,
        ["760"] = Magenta,
        ["763"] = Blue,
        ["764"] = Blue,
        ["765"] = Blue,
        ["767"] = Grey,
        ["768"] = Blue,
        ["770"] = Blue,
        ["771"] = Blue,
        ["773"] = Orange,
        ["774"] = Orange,
        ["776"] = Magenta,
        ["778"] = Green,
        ["781"] = Red,
        ["782"] = Red,
        ["783"] = Red,
        ["793"] = Red,
        ["794"] = Red,
        ["796"] = Green,
        ["797"] = Orange,
        ["798"] = Green,
        ["799"] = Blue,
    };

    public static string BadgeColor(string? routeNumber)
        => Get(routeNumber).Badge;

    public static string LineColor(string? routeNumber)
        => Get(routeNumber).Line;

    public static string TextColor(string? routeNumber)
        => Get(routeNumber).Text;

    public static RouteColorInfo Get(string? routeNumber)
    {
        var key = (routeNumber ?? "").Trim().ToUpperInvariant();
        var line = LineColors.TryGetValue(key, out var color) ? color : Fallback;
        var badge = IsBairro(key) ? Yellow : line;
        return new RouteColorInfo(badge, line, TextColorFor(badge));
    }

    private static bool IsBairro(string routeNumber)
        => routeNumber.Length > 1 && routeNumber[^1] == 'B';

    private static string TextColorFor(string background)
    {
        // Os números das carreiras são brancos sobre as cores oficiais.
        // O amarelo dos bairros é a exceção: branco sobre amarelo não se lê,
        // por isso mantém o texto escuro.
        return background.Equals(Yellow, StringComparison.OrdinalIgnoreCase)
            ? "#111111"
            : "#FFFFFF";
    }
}

public readonly record struct RouteColorInfo(string Badge, string Line, string Text);

public sealed class RouteColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var number = value as string ?? "";
        var mode = parameter as string ?? "";
        var color = mode.Equals("text", StringComparison.OrdinalIgnoreCase)
            ? RouteColors.TextColor(number)
            : mode.Equals("line", StringComparison.OrdinalIgnoreCase)
                ? RouteColors.LineColor(number)
                : RouteColors.BadgeColor(number);
        return Color.FromArgb(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
