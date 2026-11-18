using System.Reflection;
using SleazeDetector.Capture;

namespace SleazeDetector.Ocr;

/// <summary>
/// Reads Marathon's monospaced HUD font by matching glyph templates, for text too small or too blurry for the generic
/// Windows OCR engine (prox chat names and their "#1234" tags).
///
/// The templates are learned from the game's own screen pixels (see Dev/GlyphHarvest): large kill feed / squad names
/// that Windows OCR reads reliably are cut into character cells and averaged per character. Because the font is
/// monospaced, a line is a grid of equal cells whose width is a fixed ratio of the cap height, so reading a line is:
/// fit the grid (pitch, phase, baseline) and pick the best-correlated template for each cell.
///
/// Matching works on luminance, not colour. Video stores colour at half resolution (4:2:0), which smears thin red
/// text, but brightness is kept at full resolution; live captures have no subsampling at all.
/// </summary>
public sealed class GlyphReader
{
    public const int GW = 8, GH = 12;

    public sealed class Template
    {
        public char Char;
        public float[] Sum = new float[GW * GH];
        public int Count;
        public float[] Norm = Array.Empty<float>();   // zero-mean, unit-length version of Sum/Count
    }

    public Dictionary<char, Template> Templates { get; } = new();
    /// <summary>Cell pitch divided by cap height; a property of the font.</summary>
    public double PitchRatio { get; set; } = 0.75;
    public bool IsReady => Templates.Count >= 20;

    /// <param name="Dark">Text darker than its background (found by trying both).</param>
    /// <param name="OffX">Grid origin relative to the box it was read in, so a moved line can be re-read cheaply.</param>
    public sealed record Result(string Text, double Score, double[] CellScores, double Pitch, double X0, double Y0, double H,
                                bool Dark = false, double OffX = 0, double OffY = 0, string Raw = "", double Sigma = 0,
                                bool Learned = false);

    // ---------- ink map ----------

    /// <summary>Luminance of a frame region with the text made positive ("ink"), whichever way the contrast goes.</summary>
    public sealed class Ink
    {
        public readonly int W, H, X0, Y0;
        public readonly float[] V;
        public Ink(int w, int h, int x0, int y0, float[] v) { W = w; H = h; X0 = x0; Y0 = y0; V = v; }

        public float At(double x, double y)
        {
            x -= X0; y -= Y0;
            if (x < 0 || y < 0 || x > W - 1 || y > H - 1) return 0;
            int ix = Math.Min((int)x, W - 2), iy = Math.Min((int)y, H - 2);
            double tx = x - ix, ty = y - iy;
            int i = iy * W + ix;
            return (float)((V[i] * (1 - tx) + V[i + 1] * tx) * (1 - ty) + (V[i + W] * (1 - tx) + V[i + W + 1] * tx) * ty);
        }
    }

    /// <param name="darkText">True for text darker than its background (red names over white walls / snow).</param>
    public static Ink MakeInk(Frame f, int x, int y, int w, int h, bool darkText)
    {
        x = Math.Max(0, x); y = Math.Max(0, y);
        w = Math.Min(w, f.Width - x); h = Math.Min(h, f.Height - y);
        if (w < 4 || h < 4) return new Ink(1, 1, x, y, new float[1]);
        var lum = new float[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                var (r, g, b) = f.Pixel(x + xx, y + yy);
                lum[yy * w + xx] = 0.299f * r + 0.587f * g + 0.114f * b;
            }
        var sorted = (float[])lum.Clone();
        Array.Sort(sorted);
        float bg = sorted[sorted.Length / 2];
        float ext = darkText ? sorted[(int)(sorted.Length * 0.03)] : sorted[(int)(sorted.Length * 0.97)];
        float range = Math.Max(12, Math.Abs(ext - bg));
        var v = new float[lum.Length];
        for (int i = 0; i < lum.Length; i++)
            v[i] = Math.Clamp((darkText ? bg - lum[i] : lum[i] - bg) / range, 0, 1);
        return new Ink(w, h, x, y, v);
    }

    /// <summary>Resample one character cell to the template grid.</summary>
    public static float[] Cell(Ink ink, double x, double y, double pitch, double h)
    {
        // Each template pixel averages the whole sub-cell it covers (k x k bilinear samples), so a glyph reads the same
        // whether it is 10 px tall (1200p recording) or 15+ px (live 1800p); point sampling would skip pixels.
        var c = new float[GW * GH];
        double sw = pitch / GW, sh = h / GH;
        int kx = Math.Clamp((int)Math.Ceiling(sw), 1, 6), ky = Math.Clamp((int)Math.Ceiling(sh), 1, 6);
        for (int gy = 0; gy < GH; gy++)
            for (int gx = 0; gx < GW; gx++)
            {
                double sum = 0;
                for (int j = 0; j < ky; j++)
                    for (int i = 0; i < kx; i++)
                        sum += ink.At(x + (gx + (i + 0.5) / kx) * sw, y + (gy + (j + 0.5) / ky) * sh);
                c[gy * GW + gx] = (float)(sum / (kx * ky));
            }
        return c;
    }

    public static float[] Normalize(float[] a)
    {
        double mean = a.Average();
        var n = new float[a.Length];
        double ss = 0;
        for (int i = 0; i < a.Length; i++) { n[i] = (float)(a[i] - mean); ss += n[i] * n[i]; }
        double len = Math.Sqrt(ss);
        if (len < 1e-3) return new float[a.Length];
        for (int i = 0; i < n.Length; i++) n[i] = (float)(n[i] / len);
        return n;
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static double Energy(float[] cell) => cell.Average();

    // ---------- reading ----------

    /// <summary>Best template for one cell; blank cells read as a space.</summary>
    public (char c, double score) Classify(float[] cell, bool digitsOnly = false)
    {
        if (Energy(cell) < 0.04) return (' ', 0.5);
        var n = Normalize(cell);
        char best = '?'; double bs = -1;
        foreach (var t in Templates.Values)
        {
            if (t.Norm.Length == 0 || (digitsOnly && !char.IsAsciiDigit(t.Char))) continue;   // unfinished (being learned)
            double s = Dot(n, t.Norm);
            if (s > bs) { bs = s; best = t.Char; }
        }
        return (best, bs);
    }

    /// <summary>
    /// Reads the text whose approximate box (screen pixels, e.g. from Windows OCR) is given. The grid is fitted by
    /// searching cap height, baseline, pitch and phase for the best total match.
    /// </summary>
    public Result? Read(Frame f, int bx, int by, int bw, int bh, bool? darkText = null)
    {
        if (!IsReady || bh < 4) return null;
        int lx = bx - f.ScreenX, ly = by - f.ScreenY;
        Result? best = null;
        foreach (bool dark in darkText is { } d ? new[] { d } : new[] { true, false })
        {
            var ink = MakeInk(f, lx - bh, ly - bh, bw + 2 * bh, bh * 3, dark);
            var r = Fit(ink, lx, ly, bw, bh);
            if (r != null && (best == null || r.Score > best.Score)) best = r with { Dark = dark };
        }
        return best == null ? null : best with { X0 = best.X0 + f.ScreenX, Y0 = best.Y0 + f.ScreenY, OffX = best.X0 - lx, OffY = best.Y0 - ly };
    }

    /// <summary>
    /// Reads a name whose exact ink box is known (from the colour mask) and whose character pitch is known (it scales
    /// with the game resolution: 1/120 of the height). The character count follows from the width, so only a small
    /// search is needed: count +-1 and phase first, then pitch / height / baseline around the best.
    /// </summary>
    public Result? ReadRun(Frame f, int bx, int by, int bw, int bh, double pitchEst)
    {
        if (!IsReady || bh < 4) return null;
        int lx = bx - f.ScreenX, ly = by - f.ScreenY;
        int nEst = Math.Max(1, (int)Math.Round((bw + 0.2 * pitchEst) / pitchEst));
        Result? best = null; bool bestDark = false; int bestN = nEst; double bestPh = 0;
        var inks = new Dictionary<bool, Ink>();
        foreach (bool dark in new[] { false, true })
        {
            var ink = inks[dark] = MakeInk(f, lx - bh, ly - bh, bw + 2 * bh, bh * 3, dark);
            for (int n = Math.Max(1, nEst - 1); n <= nEst + 1; n++)
                for (double ph = -0.2; ph <= 0.2001; ph += 0.1)
                {
                    double pitch = (bw + 0.2 * pitchEst) / n, h = pitch / PitchRatio;
                    var r = ReadGrid(ink, lx - 0.1 * pitch + ph * pitch, ly + (bh - h) / 2, pitch, h, n);
                    if (best == null || r.Score > best.Score) { best = r; bestDark = dark; bestN = n; bestPh = ph; }
                }
        }
        var inkB = inks[bestDark];
        double p0 = (bw + 0.2 * pitchEst) / bestN;
        for (double ps = 0.97; ps <= 1.0301; ps += 0.03)
            for (double hs = 0.94; hs <= 1.0601; hs += 0.06)
                for (double dy = -0.08; dy <= 0.0801; dy += 0.08)
                    for (double ph = bestPh - 0.1; ph <= bestPh + 0.1001; ph += 0.05)
                    {
                        double pitch = p0 * ps, h = pitch / PitchRatio * hs;
                        var r = ReadGrid(inkB, lx - 0.1 * p0 + ph * p0, ly + (bh - h) / 2 + dy * h, pitch, h, bestN);
                        if (r.Score > best!.Score) best = r;
                    }
        var fin = ReadGrid(inkB, best!.X0, best.Y0, best.Pitch, best.H, best.CellScores.Length, jitter: true);
        return fin with { Dark = bestDark, X0 = fin.X0 + f.ScreenX, Y0 = fin.Y0 + f.ScreenY, OffX = fin.X0 - lx, OffY = fin.Y0 - ly };
    }

    /// <summary>
    /// Re-reads a line read before (possibly moved): only a small search around the previous grid, so tracking a
    /// visible name every few frames costs a fraction of a full read.
    /// </summary>
    public Result? ReRead(Frame f, int bx, int by, int bw, int bh, Result prev)
    {
        if (!IsReady) return null;
        int lx = bx - f.ScreenX, ly = by - f.ScreenY;
        var ink = MakeInk(f, lx - bh, ly - bh, bw + 2 * bh, bh * 3, prev.Dark);
        int n = prev.CellScores.Length;
        Result? best = null;
        for (double dx = -0.15; dx <= 0.1501; dx += 0.05)
            for (double dy = -0.1; dy <= 0.1001; dy += 0.05)
            {
                var r = ReadGrid(ink, lx + prev.OffX + dx * prev.Pitch, ly + prev.OffY + dy * prev.H, prev.Pitch, prev.H, n);
                if (best == null || r.Score > best.Score) best = r;
            }
        var fin = ReadGrid(ink, best!.X0, best.Y0, prev.Pitch, prev.H, n, jitter: true);
        return fin with { Dark = prev.Dark, X0 = fin.X0 + f.ScreenX, Y0 = fin.Y0 + f.ScreenY, OffX = fin.X0 - lx, OffY = fin.Y0 - ly };
    }

    /// <summary>
    /// The font is monospaced, so the box width divided by the character count gives the pitch far more reliably than
    /// the OCR box height (Windows draws boxes with context-dependent padding). Every plausible character count is
    /// tried; glyph height follows from the pitch via the font's ratio. A coarse search over count, height, baseline
    /// and phase is refined around the best candidate.
    /// </summary>
    private Result? Fit(Ink ink, double bx, double by, double bw, double bh)
    {
        Result? best = null;
        (int n, double hs, double dy, double ph) b = default;
        int nMin = Math.Max(3, (int)Math.Floor(bw / (bh * PitchRatio * 1.15)));
        int nMax = Math.Max(nMin, (int)Math.Ceiling(bw / (bh * PitchRatio * 0.55)));
        for (int n = nMin; n <= Math.Min(nMax, 32); n++)
            for (double hs = 0.92; hs <= 1.0801; hs += 0.08)
                for (double dy = -0.3; dy <= 0.3001; dy += 0.15)
                    for (double ph = -0.25; ph <= 0.2501; ph += 0.25)
                    {
                        var r = Try(ink, bx, by, bw, bh, n, hs, dy, 1.0, ph);
                        if (best == null || r.Score > best.Score) { best = r; b = (n, hs, dy, ph); }
                    }
        if (best == null) return null;
        for (double hs = b.hs - 0.04; hs <= b.hs + 0.0401; hs += 0.04)
            for (double dy = b.dy - 0.075; dy <= b.dy + 0.0751; dy += 0.075)
                for (double ps = 0.97; ps <= 1.0301; ps += 0.015)
                    for (double ph = b.ph - 0.125; ph <= b.ph + 0.1251; ph += 0.0625)
                    {
                        var r = Try(ink, bx, by, bw, bh, b.n, hs, dy, ps, ph);
                        if (r.Score > best.Score) best = r;
                    }
        // Final read: let each cell shift slightly so a small pitch error doesn't drift the last characters off.
        return ReadGrid(ink, best.X0, best.Y0, best.Pitch, best.H, best.CellScores.Length, jitter: true);
    }

    private Result Try(Ink ink, double bx, double by, double bw, double bh, int n, double hs, double dy, double ps, double ph)
    {
        double pitch = bw / n * ps;
        double h = pitch / PitchRatio * hs;
        double y = by + (bh - h) / 2 + dy * h;
        return ReadGrid(ink, bx + ph * pitch, y, pitch, h, n);
    }

    public Result ReadGrid(Ink ink, double x0, double y, double pitch, double h, int n, bool jitter = false)
    {
        var chars = new char[n];
        var scores = new double[n];
        double total = 0;
        int tagAt = -1;
        for (int i = 0; i < n; i++)
        {
            // A Bungie name tag is '#' plus exactly four digits: those cells are only compared with digit templates,
            // and nothing after them belongs to the name.
            bool digit = tagAt >= 0 && i > tagAt && i <= tagAt + 4;
            if (tagAt >= 0 && i > tagAt + 4) { n = i; break; }
            var (c, s) = Classify(Cell(ink, x0 + i * pitch, y, pitch, h), digit);
            if (jitter)
                foreach (var (jx, jy) in new[] { (-0.12, 0.0), (0.12, 0.0), (0.0, -0.06), (0.0, 0.06), (-0.12, -0.06), (0.12, 0.06), (-0.12, 0.06), (0.12, -0.06) })
                {
                    var (c2, s2) = Classify(Cell(ink, x0 + (i + jx) * pitch, y + jy * h, pitch, h), digit);
                    if (s2 > s) (c, s) = (c2, s2);
                }
            if (c == '#' && tagAt < 0 && i >= 3) tagAt = i;
            chars[i] = c; scores[i] = s; total += s;
        }
        var raw = new string(chars, 0, n);
        return new Result(raw.Trim(), total / n, scores[..n], pitch, x0, y, h, Raw: raw);
    }

    /// <summary>Score of a known label on a grid; used while learning templates.</summary>
    public double ScoreLabel(Ink ink, double x0, double y, double pitch, double h, string label)
    {
        double total = 0; int k = 0;
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] == ' ' || !Templates.TryGetValue(label[i], out var t)) continue;
            total += Dot(Normalize(Cell(ink, x0 + i * pitch, y, pitch, h)), t.Norm); k++;
        }
        return k == 0 ? -1 : total / k;
    }

    // ---------- persistence ----------

    public void Finish()
    {
        foreach (var t in Templates.Values)
            t.Norm = Normalize(t.Sum.Select(v => v / Math.Max(1, t.Count)).ToArray());
    }

    public void Save(string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x48504C47); // "GLPH"
        w.Write(GW); w.Write(GH); w.Write(PitchRatio); w.Write(Templates.Count);
        foreach (var t in Templates.Values.OrderBy(t => t.Char))
        {
            w.Write(t.Char); w.Write(t.Count);
            foreach (var v in t.Sum) w.Write(v);
        }
    }

    public static GlyphReader Load(Stream s)
    {
        using var r = new BinaryReader(s);
        if (r.ReadInt32() != 0x48504C47 || r.ReadInt32() != GW || r.ReadInt32() != GH) throw new InvalidDataException("Bad glyph file");
        var g = new GlyphReader { PitchRatio = r.ReadDouble() };
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var t = new Template { Char = r.ReadChar(), Count = r.ReadInt32() };
            for (int j = 0; j < t.Sum.Length; j++) t.Sum[j] = r.ReadSingle();
            g.Templates[t.Char] = t;
        }
        g.Finish();
        return g;
    }

    /// <summary>Templates shipped inside the exe (Assets/glyphs.bin), or an empty reader if none were built yet.</summary>
    public static GlyphReader LoadEmbedded()
    {
        try
        {
            // Dev override: test freshly harvested templates without rebuilding.
            if (Environment.GetEnvironmentVariable("SLEAZE_GLYPHS") is { Length: > 0 } file && File.Exists(file))
            {
                using var fs = File.OpenRead(file);
                return Load(fs);
            }
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SleazeDetector.Assets.glyphs.bin");
            if (s != null) return Load(s);
        }
        catch (Exception ex) { Core.Log.Error("Glyph templates failed to load", ex); }
        return new GlyphReader();
    }
}
