using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SleazeDetector.Reputation;
using SleazeDetector.UI;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: renders one meter at a given scale and prints where its visible ink sits inside its layout box, to check
/// that centering the box on a name centres what you see.
///   SleazeDetector --meter-probe out.png [scale]
/// </summary>
public static class MeterProbe
{
    public static int Run(string[] args)
    {
        double scale = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
        var m = new TrustMeter(scale);
        m.Set(new RepSummary { Key = "X", Good = 1, WeightedGood = 1, Trust = 0.56, MyVerdict = Verdicts.Good });
        m.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        m.Arrange(new Rect(m.DesiredSize));
        int w = (int)Math.Ceiling(m.DesiredSize.Width * 4), h = (int)Math.Ceiling(m.DesiredSize.Height * 4);
        var bmp = new RenderTargetBitmap(w, h, 96 * 4, 96 * 4, PixelFormats.Pbgra32);
        bmp.Render(m);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        // Bright pixels = the segments and text (the background is near black).
        int top = -1, bottom = -1;
        for (int y = 0; y < h; y++)
            for (int x = 6; x < w - 6; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i] + px[i + 1] + px[i + 2] > 300) { if (top < 0) top = y; bottom = y; }
            }
        Console.WriteLine($"box {m.DesiredSize.Width:0.0}x{m.DesiredSize.Height:0.0} DIP; ink rows {top / 4.0:0.00}..{bottom / 4.0:0.00}; ink centre {(top + bottom) / 8.0:0.00} vs box centre {m.DesiredSize.Height / 2:0.00}");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(args[0]);
        enc.Save(fs);
        return 0;
    }
}
