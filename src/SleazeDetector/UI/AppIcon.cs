using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SleazeDetector.Core;

namespace SleazeDetector.UI;

/// <summary>Window icon: the Rook-killer profile icon from the game files, or the app's own icon without the game.</summary>
public static class AppIcon
{
    private static ImageSource Fallback => BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

    public static void Apply(Window w)
    {
        w.Icon = GameAssets.RoundRookIcon() ?? Fallback;
        if (GameAssets.RookKillerIcon != null) return;
        GameAssets.LoadRookKillerIconAsync().ContinueWith(_ => w.Dispatcher.BeginInvoke(() =>
        {
            if (GameAssets.RoundRookIcon() is { } icon) w.Icon = icon;
        }));
    }
}
