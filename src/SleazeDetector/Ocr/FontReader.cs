using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SleazeDetector.Capture;
using SleazeDetector.Detection;

namespace SleazeDetector.Ocr;

/// <summary>
/// Reads HUD runner names by synthesis, using the game's own HUD font (PP Fraktion Mono Bold, loaded from the user's
/// install): for a guessed grid (pitch, cap height, position, blur) every character is rendered exactly as it would
/// land on the screen's pixels (box-filtered to the pixel grid at its sub-pixel phase, then blurred like the capture)
/// and the character whose rendering best explains each cell of the line wins. The grid itself is fitted to the line
/// the same way. Nothing is learned from screenshots, so letters that differ in a few pixels only (B / 8, D / O, I / 1,
/// E / F, R / P, the underscore) are told apart by their true shapes.
///
/// The image the glyphs are compared with is the line projected onto its text colour: (pixel - background) along
/// (text colour - background). That keeps red names readable on orange walls or white snow, where plain brightness
/// barely differs, and ignores scenery of other colours.
/// </summary>
public sealed class FontReader
{
    public const string Charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_#-.*+";
    private const int CapPx = 72;                     // cap height of the high-resolution glyph images
    private const int Phases = 4;                      // sub-pixel phases per axis

    private readonly char[] _chars = Charset.ToCharArray();
    private readonly int _gw, _gh;
    private readonly double _adv, _padX, _padTop;
    private readonly double[][] _sat;                  // per char: summed-area table, (gw+1) x (gh+1)

    /// <summary>Character pitch divided by cap height, from the font's metrics.</summary>
    public double PitchRatio => _adv / CapPx;

    private FontReader(int gw, int gh, double adv, double padX, double padTop, double[][] sat)
    {
        _gw = gw; _gh = gh; _adv = adv; _padX = padX; _padTop = padTop; _sat = sat;
    }

    // ---------- loading ----------

    /// <summary>Loads the HUD font from the game install and renders its glyphs; null when unavailable.</summary>
    public static FontReader? Load()
    {
        var file = Core.GameAssets.HudFontFile();
        if (file == null) return null;
        FontReader? r = null;
        Exception? err = null;
        // WPF rendering wants an STA thread; do it on one of our own so this works from any caller.
        var th = new Thread(() =>
        {
            try { r = Render(file); }
            catch (Exception ex) { err = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.IsBackground = true;
        th.Start();
        th.Join();
        if (err != null) Core.Log.Info($"HUD font: rendering failed: {err.GetType().Name}: {err.Message}");
        else if (r != null) Core.Log.Info("HUD font: ready (reading names with the game's own font)");
        return r;
    }

    private static FontReader? Render(string file)
    {
        var gt = new GlyphTypeface(new Uri(file));
        double em = CapPx / gt.CapsHeight;
        if (!gt.CharacterToGlyphMap.TryGetValue('0', out var zero)) return null;
        double adv = gt.AdvanceWidths[zero] * em;
        double padX = adv * 0.3, padTop = CapPx * 0.4;
        int gw = (int)Math.Ceiling(adv * 1.6), gh = (int)Math.Ceiling(CapPx * 1.9);
        var sats = new double[Charset.Length][];
        for (int k = 0; k < Charset.Length; k++)
        {
            if (!gt.CharacterToGlyphMap.TryGetValue(Charset[k], out var gi)) return null;
            var geo = gt.GetGlyphOutline(gi, em, em);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, gw, gh));
                dc.PushTransform(new TranslateTransform(padX, padTop + CapPx));
                dc.DrawGeometry(Brushes.White, null, geo);
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(gw, gh, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var px = new byte[gw * gh * 4];
            bmp.CopyPixels(px, gw * 4, 0);
            var sat = new double[(gw + 1) * (gh + 1)];
            for (int y = 0; y < gh; y++)
            {
                double row = 0;
                for (int x = 0; x < gw; x++)
                {
                    row += px[(y * gw + x) * 4 + 1] / 255.0;
                    sat[(y + 1) * (gw + 1) + x + 1] = sat[y * (gw + 1) + x + 1] + row;
                }
            }
            sats[k] = sat;
        }
        var fr = new FontReader(gw, gh, adv, padX, padTop, sats);
        // Sanity check: 'H' must fill the cap band and nothing above it.
        int h = Charset.IndexOf('H');
        double inBand = fr.Area(h, padX, padTop, padX + adv, padTop + CapPx), above = fr.Area(h, 0, 0, gw, padTop - 2);
        if (inBand < adv * CapPx * 0.2 || above > inBand * 0.02) { Core.Log.Info("HUD font: glyph placement check failed"); return null; }
        return fr;
    }

    // ---------- glyph sampling ----------

    private double Sat(int k, double x, double y)
    {
        x = Math.Clamp(x, 0, _gw); y = Math.Clamp(y, 0, _gh);
        int ix = Math.Min((int)x, _gw - 1), iy = Math.Min((int)y, _gh - 1);
        double fx = x - ix, fy = y - iy;
        var s = _sat[k];
        int w = _gw + 1, i = iy * w + ix;
        return (s[i] * (1 - fx) + s[i + 1] * fx) * (1 - fy) + (s[i + w] * (1 - fx) + s[i + w + 1] * fx) * fy;
    }

    private double Area(int k, double x0, double y0, double x1, double y1) =>
        Sat(k, x1, y1) - Sat(k, x0, y1) - Sat(k, x1, y0) + Sat(k, x0, y0);

    /// <summary>All templates for one (pitch, cap, blur), quantised so nearby grids share them. Built lazily.</summary>
    private sealed class Bank
    {
        public double P, C, S;
        public int Kx0, Ky0, Kw, Kh;
        public float[]?[] T = null!;          // [char * Phases^2 + phase]
        public double[] SumT = null!, SumTT = null!;
        public float[] Kernel = Array.Empty<float>();
        public long Used;
    }

    private readonly Dictionary<(int, int, int), Bank> _banks = new();
    private float[] _win = Array.Empty<float>();
    private long _clock;

    private Bank GetBank(double p, double c, double s)
    {
        // Glyph scale changes little within a step (1.5% of a 15 px cell is a fifth of a pixel); cell positions use the
        // exact pitch, so only the shapes are shared between nearby grids.
        var key = ((int)Math.Round(Math.Log(p) / Math.Log(1.015)), (int)Math.Round(Math.Log(c) / Math.Log(1.02)), (int)Math.Round(s * 5));
        if (_banks.TryGetValue(key, out var b)) { b.Used = ++_clock; return b; }
        if (_banks.Count >= 24) _banks.Remove(_banks.MinBy(kv => kv.Value.Used).Key);
        p = Math.Pow(1.015, key.Item1); c = Math.Pow(1.02, key.Item2); s = key.Item3 / 5.0;
        BanksMade++;
        b = new Bank { P = p, C = c, S = s, Used = ++_clock };
        int r = s > 0.05 ? (int)Math.Ceiling(2.5 * s) : 0;
        b.Kx0 = (int)Math.Floor(-0.2 * p) - r - 1; b.Kw = (int)Math.Ceiling(1.4 * p) + 2 * r + 3;
        b.Ky0 = (int)Math.Floor(-0.3 * c) - r - 1; b.Kh = (int)Math.Ceiling(1.7 * c) + 2 * r + 3;
        int n = _chars.Length * Phases * Phases;
        b.T = new float[n][]; b.SumT = new double[n]; b.SumTT = new double[n];
        if (r > 0)
        {
            b.Kernel = new float[2 * r + 1];
            double tot = 0;
            for (int i = -r; i <= r; i++) tot += b.Kernel[i + r] = (float)Math.Exp(-i * i / (2 * s * s));
            for (int i = 0; i < b.Kernel.Length; i++) b.Kernel[i] = (float)(b.Kernel[i] / tot);
        }
        _banks[key] = b;
        return b;
    }

    private float[] Template(Bank b, int k, int fx, int fy)
    {
        int idx = (k * Phases + fx) * Phases + fy;
        var t = b.T[idx];
        if (t != null) return t;
        // Separable box filter: for each pixel column, the glyph's coverage between its left and right edge is
        // integrated over the high-resolution rows once (column profile), then every vertical phase is cut from it.
        Templates++;
        double sx = _adv / b.P, sy = CapPx / b.C, area = sx * sy;
        double ox = fx / (double)Phases;
        var sat = _sat[k];
        int w1 = _gw + 1;
        var col = new double[_gh + 1];
        var ts = new float[Phases][];
        for (int q = 0; q < Phases; q++) ts[q] = new float[b.Kw * b.Kh];
        for (int m = 0; m < b.Kw; m++)
        {
            double xa = (b.Kx0 + m - ox) * sx + _padX, xb = xa + sx;
            if (xb <= 0 || xa >= _gw) continue;
            xa = Math.Clamp(xa, 0, _gw); xb = Math.Clamp(xb, 0, _gw);
            int ia = Math.Min((int)xa, _gw - 1), ib = Math.Min((int)xb, _gw - 1);
            double fa = xa - ia, fb = xb - ib;
            for (int y = 0; y <= _gh; y++)
            {
                int row = y * w1;
                col[y] = sat[row + ib] * (1 - fb) + sat[row + ib + 1] * fb - sat[row + ia] * (1 - fa) - sat[row + ia + 1] * fa;
            }
            for (int q = 0; q < Phases; q++)
            {
                double oy = q / (double)Phases;
                var tq = ts[q];
                for (int l = 0; l < b.Kh; l++)
                {
                    double ya = (b.Ky0 + l - oy) * sy + _padTop, yb = ya + sy;
                    if (yb <= 0 || ya >= _gh) continue;
                    tq[l * b.Kw + m] = (float)((ColAt(col, yb) - ColAt(col, ya)) / area);
                }
            }
        }
        for (int q = 0; q < Phases; q++)
        {
            var tq = b.Kernel.Length > 1 ? Blur(ts[q], b.Kw, b.Kh, b.Kernel) : ts[q];
            double st = 0, stt = 0;
            foreach (var v in tq) { st += v; stt += v * v; }
            int iq = (k * Phases + fx) * Phases + q;
            b.SumT[iq] = st; b.SumTT[iq] = stt;
            b.T[iq] = tq;
        }
        return b.T[idx]!;
    }

    private double ColAt(double[] col, double y)
    {
        y = Math.Clamp(y, 0, _gh);
        int i = Math.Min((int)y, _gh - 1);
        double f = y - i;
        return col[i] * (1 - f) + col[i + 1] * f;
    }

    private static float[] Blur(float[] a, int w, int h, float[] k)
    {
        int r = k.Length / 2;
        var tmp = new float[a.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double s = 0;
                for (int i = -r; i <= r; i++) { int xx = x + i; if (xx >= 0 && xx < w) s += a[y * w + xx] * k[i + r]; }
                tmp[y * w + x] = (float)s;
            }
        var o = new float[a.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double s = 0;
                for (int i = -r; i <= r; i++) { int yy = y + i; if (yy >= 0 && yy < h) s += tmp[yy * w + x] * k[i + r]; }
                o[y * w + x] = (float)s;
            }
        return o;
    }

    // ---------- the line image ----------

    /// <summary>The line's pixels projected onto its text colour (0 = background, 1 = text), with margins.</summary>
    private sealed class Obs
    {
        public int W, H, X0, Y0;     // X0/Y0: frame-local position of obs pixel (0,0)
        public int CoreX0, CoreY0, CoreX1, CoreY1;   // the line itself (box plus a little), where fits are scored
        public float[] V = null!;
        public float At(int x, int y) => x < 0 || y < 0 || x >= W || y >= H ? 0 : V[y * W + x];
    }

    private static Obs MakeObs(Frame f, int lx, int ly, int bw, int bh, int mx, int my)
    {
        var o = new Obs { X0 = lx - mx, Y0 = ly - my, W = bw + 2 * mx, H = bh + 2 * my };
        int padX = Math.Max(2, bh / 3), padY = Math.Max(2, bh / 3);
        o.CoreX0 = Math.Max(0, mx - padX); o.CoreX1 = Math.Min(o.W, mx + bw + padX);
        o.CoreY0 = Math.Max(0, my - padY); o.CoreY1 = Math.Min(o.H, my + bh + padY);
        o.V = new float[o.W * o.H];
        // Text colour: the name-coloured pixels of the box (red or lime, whichever there is more of).
        double rr = 0, gg = 0, bb = 0; int nRed = 0, nLime = 0;
        double lr = 0, lg = 0, lb = 0;
        for (int y = Math.Max(0, ly); y < Math.Min(f.Height, ly + bh); y++)
            for (int x = Math.Max(0, lx); x < Math.Min(f.Width, lx + bw); x++)
            {
                int i = (y * f.Width + x) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                if (HudRuns.IsRed(r, g, b)) { rr += r; gg += g; bb += b; nRed++; }
                else if (HudRuns.IsLime(r, g, b)) { lr += r; lg += g; lb += b; nLime++; }
            }
        double tr, tg, tb;
        if (nRed >= nLime && nRed > 0) { tr = rr / nRed; tg = gg / nRed; tb = bb / nRed; }
        else if (nLime > 0) { tr = lr / nLime; tg = lg / nLime; tb = lb / nLime; }
        else { tr = tg = tb = 255; }
        // Background: the median of everything around that is not text-coloured.
        var rs = new List<int>(o.W * o.H); var gs = new List<int>(o.W * o.H); var bs = new List<int>(o.W * o.H);
        for (int y = Math.Max(0, o.Y0); y < Math.Min(f.Height, o.Y0 + o.H); y++)
            for (int x = Math.Max(0, o.X0); x < Math.Min(f.Width, o.X0 + o.W); x++)
            {
                int i = (y * f.Width + x) * 4;
                int b = f.Bgra[i], g = f.Bgra[i + 1], r = f.Bgra[i + 2];
                if (HudRuns.IsRed(r, g, b) || HudRuns.IsLime(r, g, b)) continue;
                if (x >= lx && x < lx + bw && y >= ly && y < ly + bh) continue;   // the text's own outline / shadow
                rs.Add(r); gs.Add(g); bs.Add(b);
            }
        if (rs.Count == 0) return o;
        rs.Sort(); gs.Sort(); bs.Sort();
        double br = rs[rs.Count / 2], bgc = gs[gs.Count / 2], bbc = bs[bs.Count / 2];
        double dr = tr - br, dg = tg - bgc, db = tb - bbc, dd = dr * dr + dg * dg + db * db;
        if (dd >= 40 * 40) LocalProjection(o, f, bh, tr, tg, tb, br, bgc, bbc);
        else
        {
            // Text whose colour hardly differs from the scenery's: fall back to brightness.
            double lumBg = 0.299 * br + 0.587 * bgc + 0.114 * bbc, lumT = 0.299 * tr + 0.587 * tg + 0.114 * tb;
            double lumScale = Math.Abs(lumT - lumBg) < 20 ? 60 : lumT - lumBg;
            for (int y = 0; y < o.H; y++)
            {
                int fy = o.Y0 + y;
                if (fy < 0 || fy >= f.Height) continue;
                for (int x = 0; x < o.W; x++)
                {
                    int fx = o.X0 + x;
                    if (fx < 0 || fx >= f.Width) continue;
                    int i = (fy * f.Width + fx) * 4;
                    double v = ((0.299 * f.Bgra[i + 2] + 0.587 * f.Bgra[i + 1] + 0.114 * f.Bgra[i]) - lumBg) / lumScale;
                    o.V[y * o.W + x] = (float)Math.Clamp(v, -0.6, 1.6);
                }
            }
        }
        // Left and right of the name's coloured box there is no text of this name, by definition of the box: a white
        // arrow, "PINGED +" or a weapon icon next to it must not be read as part of its first or last letter.
        // A margin of a third of the line height: on some backgrounds the colour mask misses the outer edge of the first
        // or last letter, while HUD text next to a name is at least a cell away.
        int margin = Math.Max(2, (int)Math.Round(0.3 * bh));
        for (int y = 0; y < o.H; y++)
            for (int x = 0; x < o.W; x++)
                if (x < mx - margin || x >= mx + bw + margin) o.V[y * o.W + x] = 0;
        return o;
    }

    /// <summary>
    /// The line projected onto its text colour against the background right behind each pixel: scenery behind a line
    /// is rarely even (an orange wall behind the first letters, dark sky behind the rest), so the background colour is
    /// estimated locally from the pixels around that are not text (text-coloured pixels and a margin around them left
    /// out), smoothed over about a line's height. Each pixel is then measured along (text colour - local background),
    /// and pixels well off that line (white HUD text, icons, other-coloured scenery) are damped.
    /// </summary>
    private static void LocalProjection(Obs o, Frame f, int bh, double tr, double tg, double tb, double gbr, double gbg, double gbb)
    {
        int W = o.W, H = o.H, N = W * H;
        var text = new bool[N];
        var inFrame = new bool[N];
        var R = new float[N]; var G = new float[N]; var B = new float[N];
        for (int y = 0; y < H; y++)
        {
            int fy = o.Y0 + y;
            if (fy < 0 || fy >= f.Height) continue;
            for (int x = 0; x < W; x++)
            {
                int fx = o.X0 + x;
                if (fx < 0 || fx >= f.Width) continue;
                int i = (fy * f.Width + fx) * 4, q = y * W + x;
                int cb = f.Bgra[i], cg = f.Bgra[i + 1], cr = f.Bgra[i + 2];
                R[q] = cr; G[q] = cg; B[q] = cb; inFrame[q] = true;
                text[q] = HudRuns.IsRed(cr, cg, cb) || HudRuns.IsLime(cr, cg, cb);
            }
        }
        int d = Math.Max(1, (int)Math.Round(bh * 0.2));
        var w = new float[N]; var wr = new float[N]; var wg = new float[N]; var wb = new float[N];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int q = y * W + x;
                if (!inFrame[q]) continue;
                bool near = false;
                for (int yy = Math.Max(0, y - d); yy <= Math.Min(H - 1, y + d) && !near; yy++)
                    for (int xx = Math.Max(0, x - d); xx <= Math.Min(W - 1, x + d) && !near; xx++)
                        near = text[yy * W + xx];
                if (near) continue;
                w[q] = 1; wr[q] = R[q]; wg[q] = G[q]; wb[q] = B[q];
            }
        int r = Math.Max(2, (int)Math.Round(bh * 0.8));
        var sw = BoxBlur(BoxBlur(w, W, H, r), W, H, r);
        var sr = BoxBlur(BoxBlur(wr, W, H, r), W, H, r);
        var sg = BoxBlur(BoxBlur(wg, W, H, r), W, H, r);
        var sb = BoxBlur(BoxBlur(wb, W, H, r), W, H, r);
        for (int q = 0; q < N; q++)
        {
            if (!inFrame[q]) { o.V[q] = 0; continue; }
            double br, bg, bb;
            if (sw[q] > 0.5f) { br = sr[q] / sw[q]; bg = sg[q] / sw[q]; bb = sb[q] / sw[q]; }
            else { br = gbr; bg = gbg; bb = gbb; }
            double dr = tr - br, dg = tg - bg, db = tb - bb, dd = dr * dr + dg * dg + db * db;
            if (dd < 30 * 30) { dr = tr - gbr; dg = tg - gbg; db = tb - gbb; dd = Math.Max(1, dr * dr + dg * dg + db * db); br = gbr; bg = gbg; bb = gbb; }
            double v = ((R[q] - br) * dr + (G[q] - bg) * dg + (B[q] - bb) * db) / dd;
            {
                double er = R[q] - br - v * dr, eg = G[q] - bg - v * dg, eb = B[q] - bb - v * db;
                double e = Math.Sqrt((er * er + eg * eg + eb * eb) / dd);
                // Off the line means not this text: brighter pixels (white HUD text, light icons) at once, darker ones
                // only when far off: stroke centres are often darker than their edges (the game's glow, video colour
                // smearing) and stay ink, while the black drop shadow under every letter is far off the line and would
                // otherwise count as ink wherever the text differs from the scenery mostly in green (red on orange).
                bool brighter = 0.299 * er + 0.587 * eg + 0.114 * eb > 0;
                double e0 = brighter ? 0.3 : 1.5;
                if (e > e0) v *= Math.Exp(-(e - e0) * (e - e0) / (0.4 * 0.4));
            }
            o.V[q] = (float)Math.Clamp(v, -0.6, 1.6);
        }
    }



    private static float[] BoxBlur(float[] a, int W, int H, int r)
    {
        var t = new float[a.Length];
        for (int y = 0; y < H; y++)
        {
            double s = 0;
            for (int x = -r; x < W + r; x++)
            {
                if (x + r < W && x + r >= 0) s += a[y * W + x + r];
                if (x - r - 1 >= 0 && x - r - 1 < W) s -= a[y * W + x - r - 1];
                if (x >= 0 && x < W) t[y * W + x] = (float)s;
            }
        }
        var o = new float[a.Length];
        for (int x = 0; x < W; x++)
        {
            double s = 0;
            for (int y = -r; y < H + r; y++)
            {
                if (y + r < H && y + r >= 0) s += t[(y + r) * W + x];
                if (y - r - 1 >= 0 && y - r - 1 < H) s -= t[(y - r - 1) * W + x];
                if (y >= 0 && y < H) o[y * W + x] = (float)s;
            }
        }
        return o;
    }

    // ---------- fitting ----------

    private struct Geo { public double X0, Y0, P, C, S; }

    private readonly struct Cell
    {
        public readonly int Ax, Ay, Fx, Fy;
        public Cell(Geo g, int i)
        {
            double cx = g.X0 + i * g.P, cy = g.Y0;
            int ax = (int)Math.Floor(cx), ay = (int)Math.Floor(cy);
            int fx = (int)Math.Round((cx - ax) * Phases), fy = (int)Math.Round((cy - ay) * Phases);
            if (fx == Phases) { fx = 0; ax++; }
            if (fy == Phases) { fy = 0; ay++; }
            Ax = ax; Ay = ay; Fx = fx; Fy = fy;
        }
    }

    /// <summary>
    /// Independent best character per cell: returns the summed explained energy, the summed window variance, and the
    /// per-cell best character / gain over a blank cell.
    /// </summary>
    private double Evaluate(Obs o, Geo g, int n, out double variance, int[]? best = null, double[]? gains = null, bool digitsOnlyAfterHash = false)
    {
        var b = GetBank(g.P, g.C, g.S);
        double total = 0; variance = 0;
        int N = b.Kw * b.Kh;
        for (int i = 0; i < n; i++)
        {
            var cell = new Cell(g, i);
            int wx = cell.Ax + b.Kx0, wy = cell.Ay + b.Ky0;
            // The cell's window copied out once (zero outside the image), so each character is one dense dot product.
            if (_win.Length < N) _win = new float[N];
            var win = _win.AsSpan(0, N);
            double so = 0, soo = 0;
            for (int l = 0; l < b.Kh; l++)
                for (int m = 0; m < b.Kw; m++) { float v = o.At(wx + m, wy + l); win[l * b.Kw + m] = v; so += v; soo += v * v; }
            double varO = soo - so * so / N;
            variance += varO;
            double bestGain = 0; int bestK = -1;
            for (int k = 0; k < _chars.Length; k++)
            {
                var t = Template(b, k, cell.Fx, cell.Fy);
                int idx = (k * Phases + cell.Fx) * Phases + cell.Fy;
                double sot = System.Numerics.Tensors.TensorPrimitives.Dot(win, t.AsSpan(0, N));
                // The line image is normalised (background 0, text colour 1), so a letter is scored at that gain: ink
                // it predicts where there is none counts against it, and a wide letter cannot soak up the edges of two
                // misaligned ones.
                double gain = 2 * sot - b.SumTT[idx];
                if (gain > bestGain) { bestGain = gain; bestK = k; }
            }
            total += bestGain;
            if (best != null) best[i] = bestK;
            if (gains != null) gains[i] = bestGain;
        }
        return total;
    }

    /// <summary>
    /// How well a grid explains the line: each cell takes its best character, the whole line is rendered from those,
    /// and one gain and offset are fitted for the line (it has one colour). The fraction of the line region's variance
    /// that this explains is the score. A grid that only fits cell by cell (letters misaligned, a wide 'W' soaking
    /// up the edges of two letters) does not fit the line as a whole.
    /// </summary>
    private double Quality(Obs o, Geo g, int n, int[]? bestOut = null)
    {
        Qualities++;
        var best = bestOut ?? new int[n];
        var gains = new double[n];
        Evaluate(o, g, n, out _, best, gains);
        return LineR2(o, g, n, best);
    }

    /// <summary>Fraction of the line region's variance explained by rendering the given characters on this grid.</summary>
    private double LineR2(Obs o, Geo g, int n, int[] best)
    {
        var b = GetBank(g.P, g.C, g.S);
        var pred = new float[o.W * o.H];
        for (int i = 0; i < n; i++)
        {
            if (best[i] < 0) continue;
            var cell = new Cell(g, i);
            var t = Template(b, best[i], cell.Fx, cell.Fy);
            int wx = cell.Ax + b.Kx0, wy = cell.Ay + b.Ky0;
            for (int l = 0; l < b.Kh; l++)
            {
                int oy = wy + l;
                if ((uint)oy >= (uint)o.H) continue;
                for (int m = 0; m < b.Kw; m++)
                {
                    int ox = wx + m;
                    if ((uint)ox >= (uint)o.W) continue;
                    pred[oy * o.W + ox] += t[l * b.Kw + m];
                }
            }
        }
        double so = 0, soo = 0, sp = 0, spp = 0, sop = 0; int cnt = 0;
        for (int y = o.CoreY0; y < o.CoreY1; y++)
            for (int x = o.CoreX0; x < o.CoreX1; x++)
            {
                int i = y * o.W + x;
                double v = o.V[i], pr = pred[i];
                so += v; soo += v * v; sp += pr; spp += pr * pr; sop += v * pr; cnt++;
            }
        if (cnt < 4) return 0;
        double vo = soo - so * so / cnt, vp = spp - sp * sp / cnt, cov = sop - so * sp / cnt;
        return vo <= 1e-9 || vp <= 1e-9 || cov <= 0 ? 0 : cov * cov / (vp * vo);
    }

    /// <param name="nParams">How many parameters to fit: 2 = position, 3 = + pitch, 5 = + cap height and blur.</param>
    private Geo Search(Obs o, Geo g, int n, double pMin, double pMax, int nParams, out double q, double[]? factors = null,
                       Func<Geo, double>? objective = null)
    {
        objective ??= c => Quality(o, c, n);
        q = objective(g);
        factors ??= nParams >= 3 ? new[] { 1.0, 0.5, 0.25 } : new[] { 0.5, 0.25 };
        foreach (var f in factors)
        {
            for (int param = 0; param < nParams; param++)
                foreach (int dir in new[] { 1, -1 })
                {
                    for (int tries = 0; tries < 3; tries++)
                    {
                        var c = g;
                        switch (param)
                        {
                            case 0: c.X0 += dir * 0.25 * g.P * f; break;
                            case 1: c.Y0 += dir * 0.25 * g.C * f; break;
                            case 2: { double r = c.C / c.P; c.P *= 1 + dir * 0.02 * f; c.C = c.P * r; break; }
                            case 3: c.C *= 1 + dir * 0.06 * f; break;
                            case 4: c.S = Math.Max(0, c.S + dir * 0.4 * f); break;
                        }
                        if (c.P < pMin || c.P > pMax || c.C < c.P / 0.98 || c.C > c.P / 0.80 || c.S > 3) break;
                        double cq = objective(c);
                        if (cq <= q + 1e-6) break;
                        g = c; q = cq;
                    }
                }
        }
        return g;
    }

    /// <summary>Final decode: neighbours included in each cell's prediction, blanks, and the "#1234" tag rule.</summary>
    private GlyphReader.Result Decode(Obs o, Geo g, int n, Frame f, int lx, int ly)
    {
        var b = GetBank(g.P, g.C, g.S);
        var best = new int[n]; var gains = new double[n];
        Evaluate(o, g, n, out _, best, gains);
        // Cells explaining far less than the line's typical letter are blanks (spaces, the end of the line).
        var sorted = gains.Where(x => x > 0).OrderBy(x => x).ToArray();
        double med = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        // A cell holding text-coloured ink is a character, however little it explains (an underscore is a thin
        // bar below the line); only cells without such ink can be blanks.
        var ink = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int cnt = 0, area = 0;
            for (int yy = (int)Math.Floor(g.Y0 - 0.05 * g.C); yy <= (int)Math.Ceiling(g.Y0 + 1.35 * g.C); yy++)
                for (int xx = (int)Math.Ceiling(g.X0 + (i + 0.15) * g.P); xx <= (int)Math.Floor(g.X0 + (i + 0.85) * g.P); xx++)
                { area++; if (o.At(xx, yy) > 0.5f) cnt++; }
            ink[i] = cnt >= Math.Max(2, area * 0.03);
        }
        for (int i = 0; i < n; i++) if (gains[i] < med * 0.12 && !ink[i]) best[i] = -1;

        var cells = Enumerable.Range(0, n).Select(i => new Cell(g, i)).ToArray();
        int N = b.Kw * b.Kh;
        var nb = new float[N];
        var rr = new float[N];
        int tagAt = -1;
        // The line has one colour, so one gain and offset are fitted for the whole line and every cell's choice is
        // made under them: a letter with more ink than is there (D for O, B for 8) costs what it overshoots, which a
        // per-cell gain would quietly absorb.
        var (la, lb) = FitLine(o, b, cells, best, n);
        double medImp = -1;
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            var imps = new List<double>();
            for (int i = 0; i < n; i++)
            {
                if (best[i] < 0 && pass > 0 && !ink[i]) continue;   // blanks stay blank
                bool digits = tagAt >= 0 && i > tagAt && i <= tagAt + 4;
                var cell = cells[i];
                int wx = cell.Ax + b.Kx0, wy = cell.Ay + b.Ky0;
                Array.Clear(nb);
                foreach (int j in new[] { i - 2, i - 1, i + 1, i + 2 })
                {
                    if (j < 0 || j >= n || best[j] < 0) continue;
                    var tj = Template(b, best[j], cells[j].Fx, cells[j].Fy);
                    int dx = cells[j].Ax - cell.Ax, dy = cells[j].Ay - cell.Ay;
                    for (int l = 0; l < b.Kh; l++)
                    {
                        int sl = l - dy;
                        if (sl < 0 || sl >= b.Kh) continue;
                        for (int m = 0; m < b.Kw; m++)
                        {
                            int sm = m - dx;
                            if (sm < 0 || sm >= b.Kw) continue;
                            nb[l * b.Kw + m] += tj[sl * b.Kw + sm];
                        }
                    }
                }
                double baseSse = 0;
                for (int l = 0; l < b.Kh; l++)
                    for (int m = 0; m < b.Kw; m++)
                    {
                        int q = l * b.Kw + m;
                        float r = (float)(o.At(wx + m, wy + l) - la - lb * nb[q]);
                        rr[q] = r; baseSse += r * r;
                    }
                double bestImp = 0; int bestK = -1;
                for (int k = 0; k < _chars.Length; k++)
                {
                    if (digits && !char.IsAsciiDigit(_chars[k])) continue;
                    var t = Template(b, k, cell.Fx, cell.Fy);
                    int idx = (k * Phases + cell.Fx) * Phases + cell.Fy;
                    double rt = System.Numerics.Tensors.TensorPrimitives.Dot(rr.AsSpan(0, N), t.AsSpan(0, N));
                    double imp = 2 * lb * rt - lb * lb * b.SumTT[idx];
                    if (imp > bestImp) { bestImp = imp; bestK = k; }
                }
                if (bestK >= 0) imps.Add(bestImp);
                // A letter has to explain clearly more than the blank cell does (unless the cell holds text ink).
                if (bestK >= 0 && medImp > 0 && bestImp < medImp * 0.15 && !digits && !ink[i]) bestK = -1;
                if (bestK != best[i]) { best[i] = bestK; changed = true; }
                if (tagAt < 0 && bestK >= 0 && _chars[bestK] == '#' && i >= 3) { tagAt = i; changed = true; }
            }
            if (medImp < 0 && imps.Count > 0) { imps.Sort(); medImp = imps[imps.Count / 2]; changed = true; }
            (la, lb) = FitLine(o, b, cells, best, n);
            if (!changed && pass > 0) break;
        }
        if (tagAt >= 0 && tagAt + 5 < n) n = tagAt + 5;

        // Scores: correlation of the final prediction with the pixels in each cell's own window.
        var chars = new char[n];
        var scores = new double[n];
        for (int i = 0; i < n; i++)
        {
            chars[i] = best[i] < 0 ? ' ' : _chars[best[i]];
            if (best[i] < 0) { scores[i] = 0.5; continue; }
            var cell = cells[i];
            int wx = cell.Ax + b.Kx0 + (int)Math.Floor(0.15 * g.P) + 1, ww = Math.Max(2, (int)Math.Round(g.P * 0.9));
            double so = 0, soo = 0, sp = 0, spp = 0, sop = 0; int cnt = 0;
            for (int yy = (int)Math.Floor(g.Y0 - 0.25 * g.C); yy <= (int)Math.Ceiling(g.Y0 + 1.3 * g.C); yy++)
                for (int xx = (int)Math.Floor(g.X0 + i * g.P - 0.05 * g.P); xx < (int)Math.Ceiling(g.X0 + (i + 1.05) * g.P); xx++)
                {
                    double v = o.At(xx, yy), pr = 0;
                    for (int j = Math.Max(0, i - 1); j <= Math.Min(n - 1, i + 1); j++)
                    {
                        if (best[j] < 0) continue;
                        var t = Template(b, best[j], cells[j].Fx, cells[j].Fy);
                        int m = xx - (cells[j].Ax + b.Kx0), l = yy - (cells[j].Ay + b.Ky0);
                        if (m >= 0 && m < b.Kw && l >= 0 && l < b.Kh) pr += t[l * b.Kw + m];
                    }
                    so += v; soo += v * v; sp += pr; spp += pr * pr; sop += v * pr; cnt++;
                }
            double cov = sop - so * sp / cnt, vo = soo - so * so / cnt, vp = spp - sp * sp / cnt;
            scores[i] = vo > 1e-9 && vp > 1e-9 ? Math.Max(0, cov / Math.Sqrt(vo * vp)) : 0;
        }
        var raw = new string(chars);
        if (DumpPath != null) Dump(o, g, best, n, cells, b, DumpPath);
        double sx = f.ScreenX + o.X0 + g.X0, sy = f.ScreenY + o.Y0 + g.Y0;
        return new GlyphReader.Result(raw.Trim(), scores.Length == 0 ? 0 : scores.Average(), scores, g.P, sx, sy, g.C,
                                      OffX: sx - (f.ScreenX + lx), OffY: sy - (f.ScreenY + ly), Raw: raw, Sigma: g.S);
    }

    public static Action<string>? Debug { get; set; }
    public static string? DebugTruth;
    public static long Templates, Qualities, BanksMade, KnownReads, Reads;
    public static string? DumpPath;

    private sealed class Prior { public double P, Ratio, Sigma; public int N; }

    private int[] Indices(string raw, int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i < raw.Length ? Array.IndexOf(_chars, raw[i]) : -1;
        return a;
    }
    private readonly Dictionary<int, Prior> _priors = new();
    private int _refine;

    /// <summary>Gain and offset of the rendered line against the pixels, over the line's own region.</summary>
    private (double a, double b) FitLine(Obs o, Bank bk, Cell[] cells, int[] best, int n)
    {
        var pred = new float[o.W * o.H];
        for (int i = 0; i < n; i++)
        {
            if (best[i] < 0) continue;
            var t = Template(bk, best[i], cells[i].Fx, cells[i].Fy);
            int wx = cells[i].Ax + bk.Kx0, wy = cells[i].Ay + bk.Ky0;
            for (int l = 0; l < bk.Kh; l++)
            {
                int y = wy + l;
                if ((uint)y >= (uint)o.H) continue;
                for (int m = 0; m < bk.Kw; m++)
                {
                    int x = wx + m;
                    if ((uint)x < (uint)o.W) pred[y * o.W + x] += t[l * bk.Kw + m];
                }
            }
        }
        double so = 0, sp = 0, spp = 0, sop = 0; int cnt = 0;
        for (int y = o.CoreY0; y < o.CoreY1; y++)
            for (int x = o.CoreX0; x < o.CoreX1; x++)
            {
                int i = y * o.W + x;
                so += o.V[i]; sp += pred[i]; spp += pred[i] * pred[i]; sop += o.V[i] * pred[i]; cnt++;
            }
        double vp = spp - sp * sp / Math.Max(1, cnt);
        double b = vp > 1e-9 ? Math.Max(0.05, (sop - so * sp / cnt) / vp) : 1;
        return ((so - b * sp) / Math.Max(1, cnt), b);
    }

    /// <summary>Dev: observed line, predicted line and their difference, stacked, 8x enlarged.</summary>
    private void Dump(Obs o, Geo g, int[] best, int n, Cell[] cells, Bank b, string path)
    {
        var pred = new float[o.W * o.H];
        for (int i = 0; i < n; i++)
        {
            if (best[i] < 0) continue;
            var t = Template(b, best[i], cells[i].Fx, cells[i].Fy);
            int wx = cells[i].Ax + b.Kx0, wy = cells[i].Ay + b.Ky0;
            for (int l = 0; l < b.Kh; l++)
                for (int m = 0; m < b.Kw; m++)
                {
                    int x = wx + m, y = wy + l;
                    if ((uint)x < (uint)o.W && (uint)y < (uint)o.H) pred[y * o.W + x] += t[l * b.Kw + m];
                }
        }
        // Fit gain/offset of pred to obs.
        double so = 0, sp = 0, spp = 0, sop = 0; int cnt = o.W * o.H;
        for (int i = 0; i < cnt; i++) { so += o.V[i]; sp += pred[i]; spp += pred[i] * pred[i]; sop += o.V[i] * pred[i]; }
        double bb = (sop - so * sp / cnt) / Math.Max(1e-9, spp - sp * sp / cnt), aa = (so - bb * sp) / cnt;
        int k = 8;
        using var bmp = new System.Drawing.Bitmap(o.W * k, o.H * k * 3);
        for (int y = 0; y < o.H; y++)
            for (int x = 0; x < o.W; x++)
            {
                double ov = o.V[y * o.W + x], pv = aa + bb * pred[y * o.W + x];
                int c1 = Math.Clamp((int)(ov * 200), 0, 255), c2 = Math.Clamp((int)(pv * 200), 0, 255);
                int d = Math.Clamp((int)((ov - pv) * 300), -255, 255);
                var cd = d >= 0 ? System.Drawing.Color.FromArgb(d, 0, 0) : System.Drawing.Color.FromArgb(0, 0, -d);
                for (int yy = 0; yy < k; yy++)
                    for (int xx = 0; xx < k; xx++)
                    {
                        bmp.SetPixel(x * k + xx, y * k + yy, System.Drawing.Color.FromArgb(c1, c1, c1));
                        bmp.SetPixel(x * k + xx, (o.H + y) * k + yy, System.Drawing.Color.FromArgb(c2, c2, c2));
                        bmp.SetPixel(x * k + xx, (2 * o.H + y) * k + yy, cd);
                    }
            }
        bmp.Save(path);
    }

    // ---------- public API (same shape as GlyphReader) ----------

    /// <summary>
    /// Reads a name whose ink box (screen pixels) is known. <paramref name="pitchEst"/> is the expected character
    /// pitch: the HUD font scales with the game height, so it is known to a few percent.
    /// </summary>
    /// <param name="tagged">Prox chat: the name ends in "#1234" and the box may include the speaker icon after it.</param>
    public GlyphReader.Result? ReadRun(Frame f, int bx, int by, int bw, int bh, double pitchEst, bool tagged = false)
    {
        if (bh < 4 || bw < 3) return null;
        int lx = bx - f.ScreenX, ly = by - f.ScreenY;
        // Pitch, cap height relative to pitch and blur are the same for every name on a screen: once learned at this
        // size they are reused, and only the position is fitted per name (pitch just a little).
        int sizeKey = (int)Math.Round(pitchEst * 4);
        // Until this size has been learned, start from the values seen on recordings and live captures (cap height
        // 1.1 pitches, blur about 0.08 pitch) and fit them as well.
        if (!_priors.TryGetValue(sizeKey, out var prior))
            _priors[sizeKey] = prior = new Prior { P = pitchEst, Ratio = 1.1, Sigma = Math.Clamp(0.08 * pitchEst, 0.5, 1.6) };
        // Per name only position and pitch are searched; cap height and blur are refined from the best reading of
        // each name while the size is new, then now and then.
        bool known = true;
        Reads++; if (known) KnownReads++;
        // The pitch itself still varies by a few percent between sessions at the same resolution (HUD scaling), so
        // it is always fitted per line.
        double pMin = pitchEst * 0.95, pMax = pitchEst * 1.05;
        int mx = (int)Math.Ceiling(pitchEst * 1.2) + 4, my = (int)Math.Ceiling(pitchEst * 1.3) + 4;
        var o = MakeObs(f, lx, ly, bw, bh, mx, my);
        var cands = new List<(double q, Geo g, int n, GlyphReader.Result r)>();
        // A speaker icon after a prox tag can add up to about two and a half cells to the box.
        int nLo = Math.Max(1, (int)Math.Floor((tagged ? bw - 2.6 * pMax : bw) / pMax)), nHi = (int)Math.Ceiling(bw / pMin) + 1;
        // Stage 1, for every plausible character count: scan where the first cell starts (it depends on the first
        // letter's side bearing; a local search from a guess gets stuck a third of a cell off) at three pitches.
        var scans = new List<(double q, Geo g, int n)>();
        for (int n = nLo; n <= nHi; n++)
        {
            // The ink box spans n cells minus the side bearings of the first and last letter (about a quarter cell).
            double fit = bw / Math.Max(0.6, n - 0.25), p = Math.Clamp(fit, pMin, pMax);
            if (Math.Abs(fit - p) > p * (known ? 0.06 : 0.08) && !(tagged && fit > p)) continue;
            var g0 = new Geo { X0 = mx - 0.12 * p, Y0 = my, P = p,
                               C = p * prior.Ratio, S = prior.Sigma };
            double bq0 = -1; var bg0 = g0;
            foreach (double ps in new[] { 0.985, 1.0, 1.015 })
                for (double dx = -0.45 * p; dx <= 0.2 * p + 0.001; dx += 0.5)
                {
                    var c = g0; c.P = Math.Clamp(p * ps, pMin, pMax); c.C = g0.C / g0.P * c.P; c.X0 = mx + dx;
                    double cq = Quality(o, c, n);
                    if (cq > bq0) { bq0 = cq; bg0 = c; }
                }
            scans.Add((bq0, bg0, n));
        }
        // Scenery in a name colour (a red light, a marker) explains next to nothing as text: give up early with a
        // reading that fails the caller's score checks, instead of fitting it in full.
        if (scans.Count == 0 || scans.Max(x => x.q) < 0.3)
        {
            Debug?.Invoke($"    font: not text (best scan q={(scans.Count == 0 ? 0 : scans.Max(x => x.q)):0.000})");
            return new GlyphReader.Result("", 0, Array.Empty<double>(), pitchEst, bx, by, pitchEst / PitchRatio);
        }
        // Stage 2: the full fit only for the two counts that scanned best.
        foreach (var (bq1, bg1, n) in scans.OrderByDescending(x => x.q).Take(2))
        {
            double bq = bq1; var bg = bg1; var g = bg1;
            g = bg;
            foreach (double dy in new[] { -1.0, -0.5, 0.5, 1.0 })
            {
                var c = g; c.Y0 = bg.Y0 + dy;
                double cq = Quality(o, c, n);
                if (cq > bq) { bq = cq; g = c; }
            }
            g = Search(o, g, n, pMin, pMax, 3, out var q, new[] { 0.125 });
            if (!known) g = Search(o, g, n, pMin, pMax, 5, out q, new[] { 0.5, 0.25 });
            var r = Decode(o, g, n, f, lx, ly);
            // With the characters read, fit the grid to that text (a smooth fit, no letter switching from step to
            // step), then read again on the refined grid.
            for (int round = 0; round < 2; round++)
            {
                var chars = Indices(r.Raw, n);
                g = Search(o, g, n, pMin, pMax, 3, out q, new[] { 0.25, 0.125, 0.0625 }, c => LineR2(o, c, n, chars));
                var r2 = Decode(o, g, n, f, lx, ly);
                if (r2.Raw == r.Raw) break;
                r = r2;
            }
            if (Debug != null && DebugTruth is { } tru && tru.Length == n)
            {
                var ti = Indices(tru, n);
                double qt = LineR2(o, g, n, ti);
                var gt = Search(o, g, n, pMin, pMax, 5, out var qt2, new[] { 0.5, 0.25, 0.125 }, c => LineR2(o, c, n, ti));
                double qd = LineR2(o, gt, n, Indices(r.Raw, n));
                Debug($"    truth '{tru}' R2 at found grid {qt:0.000} vs decoded {LineR2(o, g, n, Indices(r.Raw, n)):0.000}; truth-fitted grid {qt2:0.000} (decoded there {qd:0.000}) x0={gt.X0 - mx:0.00} y0={gt.Y0 - my:0.00} p={gt.P:0.000} c={gt.C:0.000} s={gt.S:0.00}; decode there '{Decode(o, gt, n, f, lx, ly).Raw}'");
            }
            Debug?.Invoke($"    font n={n} q={q:0.000} '{r.Raw}' x0={g.X0 - mx:0.00} y0={g.Y0 - my:0.00} p={g.P:0.000} c={g.C:0.000} s={g.S:0.00} known={known}");
            cands.Add((q, g, n, r));
        }
        if (cands.Count == 0) return null;
        // The box is the ink's extent: its first and last cells hold letters. A grid with a blank cell at either end
        // has the wrong character count, however well it fits.
        var pick = cands.OrderByDescending(c => c.r.Raw.Length > 0 && c.r.Raw[0] != ' ' && c.r.Raw[^1] != ' ')
                        .ThenByDescending(c => c.q).First();
        var bestR = pick.r; var bestG = pick.g; int bestN = pick.n; double bestQ = pick.q;
        // Learn the screen's pitch, cap ratio and blur from clean reads (and keep refining them now and then).
        if (bestR.Score >= 0.7 && bestQ >= 0.5 && (prior.N < 10 || ++_refine % 8 == 0))
        {
            var chars = Indices(bestR.Raw, bestN);
            var g = Search(o, bestG, bestN, pitchEst * 0.95, pitchEst * 1.05, 5, out _,
                           prior.N < 3 ? new[] { 0.5, 0.25 } : new[] { 0.25 }, c => LineR2(o, c, bestN, chars));
            var pr = prior;
            double w = 1.0 / Math.Min(pr.N + 2, 10);
            pr.P = pr.P * (1 - w) + g.P * w;
            pr.Ratio = pr.Ratio * (1 - w) + g.C / g.P * w;
            pr.Sigma = pr.Sigma * (1 - w) + g.S * w;
            pr.N++;
            _priors[sizeKey] = pr;
        }
        return bestR;
    }

    /// <summary>Re-reads a line read before (possibly moved a little): same grid size and blur, small position search.</summary>
    public GlyphReader.Result? ReRead(Frame f, int bx, int by, int bw, int bh, GlyphReader.Result prev)
    {
        int lx = bx - f.ScreenX, ly = by - f.ScreenY;
        int n = prev.CellScores.Length;
        if (n == 0) return null;
        int mx = (int)Math.Ceiling(prev.Pitch * 1.2) + 4, my = (int)Math.Ceiling(prev.Pitch * 1.3) + 4;
        int w = Math.Max(bw, (int)Math.Ceiling(n * prev.Pitch));
        var o = MakeObs(f, lx, ly, w, bh, mx, my);
        var g = new Geo { X0 = mx + prev.OffX, Y0 = my + prev.OffY, P = prev.Pitch, C = prev.H, S = prev.Sigma };
        g = Search(o, g, n, prev.Pitch * 0.999, prev.Pitch * 1.001, 2, out _);
        return Decode(o, g, n, f, lx, ly);
    }
}
