using SleazeDetector.Capture;
using SleazeDetector.Core;

namespace SleazeDetector.Detection;

/// <summary>
/// Tells whether the in-match HUD is up from the ability bar at the bottom centre of the screen: a row of slot boxes
/// at fixed positions that every shell has, whatever its icons and whatever state the slots are in (ready, recharging,
/// active). Menus, the lobby, loading and spectating have nothing there.
///
/// Only the outlines are checked: each side edge of the first two slots (the third can be an empty, faint slot) must
/// be a continuous vertical line, stronger than the picture a few pixels to either side of it. Measured on recordings:
/// the bar scores 0.5 to 1 in most frames (lower on bright scenery, where its outline washes out), menus and the vault
/// at most 0.26.
/// </summary>
public static class AbilityBar
{
    // 1200p reference, x relative to the screen centre: the side edges of the first two slots, and their height.
    private static readonly int[] Edges = { -108, -47, -43, 43 };
    private const int SlotY0 = 1075, SlotY1 = 1122;
    private const int Shift = 3, Side = 5;
    private const int AreaX0 = -108 - Shift - Side - 2, AreaX1 = 43 + Shift + Side + 3;
    private const int AreaY0 = SlotY0 - Shift - 2, AreaY1 = SlotY1 + Shift + 2;

    /// <summary>A score at or above this means the bar is there.</summary>
    public const double Threshold = 0.6;

    /// <summary>Screen box <see cref="Score"/> reads for this game window.</summary>
    public static (int X, int Y, int W, int H) Box(GameWindow game) => ShellDetector.SampleBox(game, AreaX0, AreaX1, AreaY0, AreaY1);

    /// <summary>0 to 1: how much of the slot outlines is visible (the share of each edge's length, worst edge).</summary>
    public static double Score(GameWindow game, Func<int, int, int, int, Frame> grab)
    {
        var g = ShellDetector.Sample(game, grab, AreaX0, AreaX1, AreaY0, AreaY1);
        // Gradient values are fourth roots of the squared magnitude (see ShellDetector.Sample).
        double floor = Math.Sqrt(40), ratio = Math.Sqrt(1.5);
        double best = 0;
        for (int dy = -Shift; dy <= Shift; dy++)
            for (int dx = -Shift; dx <= Shift; dx++)
            {
                double worst = 1;
                foreach (int e in Edges)
                {
                    int x = e + dx - AreaX0, n = 0, total = 0;
                    for (int y = SlotY0 + 4 + dy - AreaY0; y < SlotY1 - 4 + dy - AreaY0; y++, total++)
                    {
                        double line = Math.Max(g[y, x - 1], Math.Max(g[y, x], g[y, x + 1]));
                        double beside = Math.Max(g[y, x - Side], g[y, x + Side]);
                        if (line > Math.Max(beside * ratio, floor)) n++;
                    }
                    worst = Math.Min(worst, n / (double)total);
                }
                best = Math.Max(best, worst);
            }
        return best;
    }
}
