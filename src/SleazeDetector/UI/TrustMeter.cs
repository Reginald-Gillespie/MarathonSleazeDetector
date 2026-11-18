using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SleazeDetector.Core;
using SleazeDetector.Reputation;

namespace SleazeDetector.UI;

/// <summary>
/// The reputation meter drawn next to names: five segments, lit by how strong the verdict is and coloured by which way
/// it goes (see Look). Grey and empty when nobody has rated the runner. Shows the game's Rook-killer icon when your own
/// rating flags them only for Rook on Rook.
/// </summary>
public sealed class TrustMeter : Border
{
    public static readonly Color Unknown = Color.FromRgb(0x6F, 0x73, 0x80);

    private readonly StackPanel _segments = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _icon = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private RepSummary? _rep;

    /// <summary>Hide the meter entirely for runners nobody has rated.</summary>
    public bool HideUnknown { get; set; }

    public TrustMeter(double scale = 1.0)
    {
        CornerRadius = new CornerRadius(0);
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0E, 0x10, 0x15));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(3 * scale, 2 * scale, 3 * scale, 2 * scale);
        SnapsToDevicePixels = true;

        double icon = 11 * scale;
        _icon.Width = _icon.Height = icon;
        _icon.Margin = new Thickness(4 * scale, -1 * scale, 0, -1 * scale);
        _icon.Clip = new EllipseGeometry(new Point(icon / 2, icon / 2), icon / 2, icon / 2);
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);

        for (int i = 0; i < 5; i++)
            _segments.Children.Add(new Rectangle { Width = 4 * scale, Height = 7 * scale, Margin = new Thickness(0, 0, i < 4 ? 1 * scale : 0, 0) });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_segments);
        row.Children.Add(_icon);
        Child = row;
        Set(null);
    }

    /// <summary>
    /// The number of lit segments is how strong the verdict is, the colour which way it goes. Leaning bad: orange, one
    /// segment per step of evidence, turning red only when fully bad (the evidence the scoring rules call full, see
    /// ScoringRules.FullAt). Leaning good: light green, turning green when fully good. An even split: one yellow
    /// segment. Nobody has rated them: grey and empty.
    /// </summary>
    public static (Color Color, int Lit) Look(double? trust)
    {
        if (trust == null) return (Unknown, 0);
        double lean = Math.Abs(trust.Value - 0.5) * 2;
        if (lean < 0.001) return (Color.FromRgb(0xFF, 0xC5, 0x3D), 1);
        bool full = lean >= 0.999;
        int lit = Math.Clamp((int)Math.Ceiling(lean * 5 - 0.001), 1, 5);
        var color = trust < 0.5
            ? (full ? Color.FromRgb(0xE5, 0x48, 0x4D) : Color.FromRgb(0xF7, 0x6B, 0x15))
            : (full ? Color.FromRgb(0x46, 0xA7, 0x58) : Color.FromRgb(0x9E, 0xE0, 0x8F));
        return (color, lit);
    }

    public void Set(RepSummary? rep)
    {
        _rep = rep;
        var trust = rep?.Trust;
        var (color, lit) = Look(trust);
        for (int i = 0; i < 5; i++)
            ((Rectangle)_segments.Children[i]).Fill = new SolidColorBrush(i < lit ? color : Color.FromArgb(0x50, 0x80, 0x86, 0x94));
        BorderBrush = new SolidColorBrush(Color.FromArgb(trust == null ? (byte)0x40 : (byte)0xA0, color.R, color.G, color.B));

        bool rookOnly = rep is { MyVerdict: Verdicts.Bad } && rep.MyFlags.Count > 0 && rep.MyFlags.All(f => f == Flags.RookOnRook);
        _icon.Source = null;
        if (rookOnly)
        {
            var img = GameAssets.RookKillerIcon;
            if (img != null) _icon.Source = img;
            else
            {
                var load = GameAssets.LoadRookKillerIconAsync();
                if (!load.IsCompleted)
                    load.ContinueWith(t => { if (t.Result != null && ReferenceEquals(_rep, rep)) Set(rep); },
                        TaskScheduler.FromCurrentSynchronizationContext());
            }
        }
        _icon.Visibility = _icon.Source != null ? Visibility.Visible : Visibility.Collapsed;
        Visibility = HideUnknown && trust == null && _icon.Source == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
