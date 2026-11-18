using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using SleazeDetector.Ocr;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: finds which font file best matches the glyph templates learned from the HUD, by rendering every
/// candidate and correlating its cells with the learned ones.
///   SleazeDetector --font-match glyphs.bin report.txt font-or-dir ...
/// </summary>
public static class FontMatch
{
    public const string Charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#_";

    /// <summary>A font rendered once at a large size; cells are area-sampled from it.</summary>
    public sealed class Rendered
    {
        public string Name = "";
        public double Advance, CapTop, Baseline;
        public Dictionary<char, (float[] ink, int w, int h, double x0)> Glyphs = new();

        public float[] Cell(char c, double dx, double dy, double sx, double sy)
        {
            var (ink, w, h, x0) = Glyphs[c];
            double capH = Baseline - CapTop;
            double cw = Advance * sx, chh = capH * sy;
            double left = x0 + (Advance - cw) / 2 + dx * Advance, top = CapTop + (capH - chh) / 2 + dy * capH;
            var cell = new float[GlyphReader.GW * GlyphReader.GH];
            for (int gy = 0; gy < GlyphReader.GH; gy++)
                for (int gx = 0; gx < GlyphReader.GW; gx++)
                {
                    // Box filter over the sub-cell: what a real low-resolution pixel sees.
                    double ax = left + gx * cw / GlyphReader.GW, bx = ax + cw / GlyphReader.GW;
                    double ay = top + gy * chh / GlyphReader.GH, by = ay + chh / GlyphReader.GH;
                    double sum = 0; int n = 0;
                    for (int y = (int)ay; y < (int)Math.Ceiling(by); y++)
                        for (int x = (int)ax; x < (int)Math.Ceiling(bx); x++)
                        {
                            if (x >= 0 && y >= 0 && x < w && y < h) sum += ink[y * w + x];
                            n++;
                        }
                    cell[gy * GlyphReader.GW + gx] = n == 0 ? 0 : (float)(sum / n);
                }
            return cell;
        }
    }

    public static Rendered? Render(string path, int px = 120)
    {
        try
        {
            using var pfc = new PrivateFontCollection();
            pfc.AddFontFile(path);
            var fam = pfc.Families[0];
            var style = fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            using var font = new Font(fam, px, style, GraphicsUnit.Pixel);
            double ascent = px * fam.GetCellAscent(style) / (double)fam.GetEmHeight(style);
            int W = px * 2, H = px * 2;
            int ox = px / 3, oy = px / 4;
            var r = new Rendered { Name = Path.GetFileNameWithoutExtension(path) + (style == FontStyle.Bold ? " (bold)" : "") };
            using (var bmp = new Bitmap(10, 10))
            using (var g = Graphics.FromImage(bmp))
            {
                var fmt = StringFormat.GenericTypographic;
                double w10 = g.MeasureString("0000000000", font, 100000, fmt).Width;
                double w1 = g.MeasureString("0", font, 100000, fmt).Width;
                r.Advance = (w10 - w1) / 9;
            }
            r.Baseline = oy + ascent;
            foreach (char c in Charset)
            {
                using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawString(c.ToString(), font, Brushes.White, ox, oy, StringFormat.GenericTypographic);
                }
                var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var bytes = new byte[W * H * 4];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                bmp.UnlockBits(data);
                var ink = new float[W * H];
                for (int i = 0; i < ink.Length; i++) ink[i] = bytes[i * 4 + 1] / 255f;
                r.Glyphs[c] = (ink, W, H, ox);
            }
            // Cap top from the ink of 'H'.
            var (hInk, hw, hh, _) = r.Glyphs['H'];
            int top = 0;
            for (int y = 0; y < hh && top == 0; y++)
                for (int x = 0; x < hw; x++) if (hInk[y * hw + x] > 0.5f) { top = y; break; }
            r.CapTop = top;
            return r;
        }
        catch { return null; }
    }

    public static double Match(Rendered r, GlyphReader learned, out (double dx, double dy, double sx, double sy) fit)
    {
        double best = -1; fit = default;
        for (double sx = 0.85; sx <= 1.1501; sx += 0.05)
            for (double sy = 1.0; sy <= 1.4001; sy += 0.05)
                for (double dx = -0.15; dx <= 0.1501; dx += 0.05)
                    for (double dy = -0.1; dy <= 0.1001; dy += 0.05)
                    {
                        double tot = 0; int n = 0;
                        foreach (var t in learned.Templates.Values)
                        {
                            if (!r.Glyphs.ContainsKey(t.Char)) continue;
                            var cell = GlyphReader.Normalize(r.Cell(t.Char, dx, dy, sx, sy));
                            double s = 0;
                            for (int i = 0; i < cell.Length; i++) s += cell[i] * t.Norm[i];
                            tot += s * Math.Log(1 + t.Count); n++;
                        }
                        double sc = n == 0 ? -1 : tot / learned.Templates.Values.Sum(t => Math.Log(1 + t.Count));
                        if (sc > best) { best = sc; fit = (dx, dy, sx, sy); }
                    }
        return best;
    }

    public static int Run(string[] args)
    {
        GlyphReader learned;
        using (var fs = File.OpenRead(args[0])) learned = GlyphReader.Load(fs);
        var files = args.Skip(2).SelectMany<string, string>(a => Directory.Exists(a)
            ? Directory.GetFiles(a).Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            : new[] { a });
        var rows = new List<(double score, string line)>();
        foreach (var f in files)
        {
            var r = Render(f);
            if (r == null) { rows.Add((-2, $"failed {f}")); continue; }
            double s = Match(r, learned, out var fit);
            double ratio = r.Advance / (r.Baseline - r.CapTop);
            rows.Add((s, $"{s:0.000}  {r.Name,-34} pitch/cap {ratio:0.000}  fit dx={fit.dx:0.00} dy={fit.dy:0.00} sx={fit.sx:0.00} sy={fit.sy:0.00}"));
        }
        var sb = new StringBuilder($"learned pitch/cap {learned.PitchRatio:0.000}\n");
        foreach (var (_, line) in rows.OrderByDescending(r => r.score)) sb.AppendLine(line);
        File.WriteAllText(args[1], sb.ToString());
        return 0;
    }
}
