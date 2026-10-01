using DeveloperIsland.Core.SystemInfo;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeveloperIsland.UI.Components;

/// <summary>The three hardware level colors from the theme, as colors and shared brushes.</summary>
public static class StatusColors
{
    private static readonly Dictionary<SystemLevel, SolidColorBrush> Brushes = [];

    public static Color Color(SystemLevel level) => (Color)Application.Current.Resources[level switch
    {
        SystemLevel.Critical => "StatusCriticalColor",
        SystemLevel.Warning => "StatusWarningColor",
        _ => "StatusGoodColor",
    }];

    public static SolidColorBrush Brush(SystemLevel level)
    {
        if (!Brushes.TryGetValue(level, out var brush))
        {
            Brushes[level] = brush = new SolidColorBrush(Color(level));
        }

        return brush;
    }

    /// <summary>Text keeps its normal color while good and takes the level color only as a warning.</summary>
    public static Brush Text(SystemLevel level, string normalKey) =>
        level == SystemLevel.Good ? (Brush)Application.Current.Resources[normalKey] : Brush(level);
}
