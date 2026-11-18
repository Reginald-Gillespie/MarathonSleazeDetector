using SleazeDetector.Capture;
using SleazeDetector.Core;

namespace SleazeDetector.Detection;

/// <summary>
/// Recognises which runner shell you are playing from the ability icons at the bottom centre of the HUD.
///
/// The icons sit in fixed slots whose fill changes all the time (white when ready, red when active, dark with a
/// countdown while recharging, partly filled while it ticks down, tinted by screen effects), but the icon's line
/// art does not. So each slot is compared by structure only: the colour gradient magnitude of the slot (strong where
/// any two colours meet, whichever is brighter) against a template learned from recordings, by normalised
/// correlation with a few pixels of alignment search. The slot border, which every shell has, is left out.
///
/// Geometry is in units of the game height around the screen centre (measured at 1200p), so any resolution
/// works: the slots are resampled to the 1200p grid before comparing.
///
/// A shell can't change during a run, so <see cref="Observe"/> collects evidence over the run and
/// <see cref="Current"/> names a shell once it has matched several times (false matches score far below the
/// threshold, see <see cref="MatchThreshold"/>). <see cref="Reset"/> at the end of a run.
/// </summary>
public sealed class ShellDetector
{
    public const string Rook = "rook";

    // 1200p reference: slot boxes (x relative to the screen centre) and the sampled area around them.
    private const double RefH = 1200;
    private static readonly (string Name, int X0, int X1)[] Slots = { ("T", -108, -47), ("Q", -43, 43) };
    private const int SlotY0 = 1075, SlotY1 = 1122;
    private const int Inset = 6;      // drop the slot border
    private const int Shift = 3;      // alignment search, +- px at 1200p
    private const int AreaX0 = -108 - Shift - 2, AreaX1 = 43 + Shift + 2, AreaY0 = SlotY0 - Shift - 2, AreaY1 = SlotY1 + Shift + 2;

    /// <summary>
    /// Both slots must correlate at least this well. Measured: the right shell 0.8-0.95 when the bar is visible,
    /// other shells at most 0.37.
    /// </summary>
    public const double MatchThreshold = 0.6;
    /// <summary>Matches needed in one run before the shell is believed.</summary>
    public const int MatchesNeeded = 3;

    public sealed class ShellTemplate
    {
        public required string Name;
        public required Dictionary<string, (int W, int H, float[] V)> Slots;
    }

    private readonly List<ShellTemplate> _shells;
    private readonly Dictionary<string, int> _matches = new();

    public ShellDetector(List<ShellTemplate> shells) => _shells = shells;

    public bool IsReady => _shells.Count > 0;

    /// <summary>The shell you are playing this run, or null while unknown.</summary>
    public string? Current { get; private set; }

    public void Reset() { lock (_matches) { _matches.Clear(); Current = null; } }

    /// <summary>Checks the ability bar once and adds the result to this run's evidence. Returns the best match.</summary>
    public (string? Shell, double Score) Observe(GameWindow game, Func<int, int, int, int, Frame> grab)
    {
        var area = Sample(game, grab);
        var best = Classify(area);
        if (best.Shell != null)
            lock (_matches)
            {
                _matches[best.Shell] = _matches.GetValueOrDefault(best.Shell) + 1;
                var top = _matches.MaxBy(kv => kv.Value);
                if (top.Value >= MatchesNeeded) Current = top.Key;
            }
        return best;
    }

    public (string? Shell, double Score) Classify(float[,] grad)
    {
        string? bestName = null; double bestScore = 0;
        foreach (var s in _shells)
        {
            double score = double.MaxValue;
            foreach (var (name, x0, x1) in Slots)
                score = Math.Min(score, SlotScore(grad, s.Slots[name], x0));
            if (score > bestScore) { bestScore = score; bestName = s.Name; }
        }
        return (bestScore >= MatchThreshold ? bestName : null, bestScore);
    }

    /// <summary>Per slot scores for one shell (diagnostics / tests).</summary>
    public double[] Scores(float[,] grad, string shell)
    {
        var s = _shells.First(x => x.Name == shell);
        return Slots.Select(sl => SlotScore(grad, s.Slots[sl.Name], sl.X0)).ToArray();
    }

    private static double SlotScore(float[,] grad, (int W, int H, float[] V) t, int slotX0)
    {
        double best = -1;
        for (int dy = -Shift; dy <= Shift; dy++)
            for (int dx = -Shift; dx <= Shift; dx++)
                best = Math.Max(best, Ncc(Patch(grad, slotX0, dx, dy, t.W, t.H), t.V));
        return best;
    }

    /// <summary>The inner part of a slot (border dropped) from the sampled area, shifted by dx, dy.</summary>
    public static float[] Patch(float[,] grad, int slotX0, int dx, int dy, int w, int h)
    {
        var p = new float[w * h];
        int ox = slotX0 + Inset - AreaX0 + dx, oy = SlotY0 + Inset - AreaY0 + dy;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                p[y * w + x] = grad[oy + y, ox + x];
        return p;
    }

    public static (int W, int H) PatchSize(string slot)
    {
        var s = Slots.First(x => x.Name == slot);
        return (s.X1 - s.X0 - 2 * Inset, SlotY1 - SlotY0 - 2 * Inset);
    }

    public static int SlotX0(string slot) => Slots.First(x => x.Name == slot).X0;
    public static IEnumerable<string> SlotNames => Slots.Select(s => s.Name);
    public static int ShiftRange => Shift;

    private static double Ncc(float[] a, float[] b)
    {
        double ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double x = a[i] - ma, y = b[i] - mb;
            ab += x * y; aa += x * x; bb += y * y;
        }
        return aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 0;
    }

    /// <summary>
    /// Grabs the ability bar area, resamples it to the 1200p grid (bilinear) and returns its colour gradient
    /// magnitude: per channel Sobel, the largest channel, square-rooted to even out the very different contrast of
    /// the slot states.
    /// </summary>
    /// <summary>Screen box that <see cref="Sample"/> grabs for this game window.</summary>
    public static (int X, int Y, int W, int H) SampleBox(GameWindow game) => SampleBox(game, AreaX0, AreaX1, AreaY0, AreaY1);

    /// <summary>Screen box covering an area given on the 1200p grid (x relative to the screen centre).</summary>
    public static (int X, int Y, int W, int H) SampleBox(GameWindow game, int ax0, int ax1, int ay0, int ay1)
    {
        double k = game.Height / RefH;
        double cx = game.X + game.Width / 2.0;
        int sx0 = (int)Math.Floor(cx + ax0 * k) - 1, sx1 = (int)Math.Ceiling(cx + ax1 * k) + 2;
        int sy0 = (int)Math.Floor(game.Y + ay0 * k) - 1, sy1 = (int)Math.Ceiling(game.Y + ay1 * k) + 2;
        return (sx0, sy0, sx1 - sx0, sy1 - sy0);
    }

    public static float[,] Sample(GameWindow game, Func<int, int, int, int, Frame> grab) =>
        Sample(game, grab, AreaX0, AreaX1, AreaY0, AreaY1);

    /// <summary>The same for any area of the 1200p grid (x relative to the screen centre); the result is indexed
    /// [y - ay0, x - ax0].</summary>
    public static float[,] Sample(GameWindow game, Func<int, int, int, int, Frame> grab, int ax0, int ax1, int ay0, int ay1)
    {
        double k = game.Height / RefH;
        double cx = game.X + game.Width / 2.0;
        var box = SampleBox(game, ax0, ax1, ay0, ay1);
        var f = grab(box.X, box.Y, box.W, box.H);
        int w = ax1 - ax0, h = ay1 - ay0;
        var rgb = new float[3, h, w];
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
            {
                double x = cx + (ax0 + u) * k - f.ScreenX, y = game.Y + (ay0 + v) * k - f.ScreenY;
                int x0 = Math.Clamp((int)Math.Floor(x), 0, f.Width - 2), y0 = Math.Clamp((int)Math.Floor(y), 0, f.Height - 2);
                double fx = Math.Clamp(x - x0, 0, 1), fy = Math.Clamp(y - y0, 0, 1);
                for (int c = 0; c < 3; c++)
                {
                    int ch = 2 - c; // BGRA -> R, G, B
                    double p00 = f.Bgra[(y0 * f.Width + x0) * 4 + ch], p10 = f.Bgra[(y0 * f.Width + x0 + 1) * 4 + ch];
                    double p01 = f.Bgra[((y0 + 1) * f.Width + x0) * 4 + ch], p11 = f.Bgra[((y0 + 1) * f.Width + x0 + 1) * 4 + ch];
                    rgb[c, v, u] = (float)((p00 * (1 - fx) + p10 * fx) * (1 - fy) + (p01 * (1 - fx) + p11 * fx) * fy);
                }
            }
        var g = new float[h, w];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                double best = 0;
                for (int c = 0; c < 3; c++)
                {
                    double gx = (rgb[c, y - 1, x + 1] + 2 * rgb[c, y, x + 1] + rgb[c, y + 1, x + 1]) - (rgb[c, y - 1, x - 1] + 2 * rgb[c, y, x - 1] + rgb[c, y + 1, x - 1]);
                    double gy = (rgb[c, y + 1, x - 1] + 2 * rgb[c, y + 1, x] + rgb[c, y + 1, x + 1]) - (rgb[c, y - 1, x - 1] + 2 * rgb[c, y - 1, x] + rgb[c, y - 1, x + 1]);
                    best = Math.Max(best, gx * gx + gy * gy);
                }
                g[y, x] = (float)Math.Sqrt(Math.Sqrt(best));
            }
        return g;
    }

    // ---------- templates (Assets/shells.bin) ----------

    public static List<ShellTemplate> LoadEmbedded()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SLEAZE_SHELLS") is { Length: > 0 } file && File.Exists(file))
            {
                using var fs = File.OpenRead(file);
                return Load(fs);
            }
            using var s = typeof(ShellDetector).Assembly.GetManifestResourceStream("SleazeDetector.Assets.shells.bin");
            return s == null ? new() : Load(s);
        }
        catch (Exception ex) { Log.Error("Could not load shell templates", ex); return new(); }
    }

    public static List<ShellTemplate> Load(Stream s)
    {
        using var r = new BinaryReader(s);
        int n = r.ReadInt32();
        var list = new List<ShellTemplate>();
        for (int i = 0; i < n; i++)
        {
            var name = r.ReadString();
            int slots = r.ReadInt32();
            var d = new Dictionary<string, (int, int, float[])>();
            for (int j = 0; j < slots; j++)
            {
                var sn = r.ReadString(); int w = r.ReadInt32(), h = r.ReadInt32();
                var v = new float[w * h];
                for (int k = 0; k < v.Length; k++) v[k] = r.ReadSingle();
                d[sn] = (w, h, v);
            }
            list.Add(new ShellTemplate { Name = name, Slots = d });
        }
        return list;
    }

    public static void Save(Stream s, List<ShellTemplate> shells)
    {
        using var w = new BinaryWriter(s);
        w.Write(shells.Count);
        foreach (var sh in shells)
        {
            w.Write(sh.Name);
            w.Write(sh.Slots.Count);
            foreach (var (sn, (ww, hh, v)) in sh.Slots)
            {
                w.Write(sn); w.Write(ww); w.Write(hh);
                foreach (var x in v) w.Write(x);
            }
        }
    }
}
