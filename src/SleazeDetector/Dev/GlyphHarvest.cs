using System.Text;
using SleazeDetector.Capture;
using SleazeDetector.Ocr;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: learns the HUD font's glyph templates from screenshots / video frames.
///   SleazeDetector --glyph-harvest out.bin atlas.png frame1.png frame2.png ...
/// Large HUD text (kill feed, squad list, pings) is read with Windows OCR; only words read identically in several
/// frames are trusted as labels. Each labelled word is cut into monospace cells and averaged per character, then
/// every word's grid is re-fitted against the current templates and outliers (misreads) are dropped, a few times.
/// </summary>
public static class GlyphHarvest
{
    private sealed record Sample(int Image, string Label, int X, int Y, int W, int H, bool Verified = false);

    public static async Task<int> RunAsync(string[] args)
    {
        string outPath = args[0], atlasPath = args[1];
        var paths = args.Skip(2).SelectMany<string, string>(a => Directory.Exists(a)
            ? Directory.GetFiles(a).Where(f => f.EndsWith(".png") || f.EndsWith(".jpg")).OrderBy(f => f)
            : new[] { a }).ToList();
        var ocr = new HudOcr();
        // Optional hand-verified labels: lines "fileprefix|TEXT" (e.g. prox frames showing "RUNNER#1234").
        var labels = Environment.GetEnvironmentVariable("GLYPH_LABELS") is { Length: > 0 } lf && File.Exists(lf)
            ? File.ReadAllLines(lf).Select(l => l.Split('|')).Where(p => p.Length == 2).Select(p => (prefix: p[0].Trim(), text: p[1].Trim())).ToList()
            : new List<(string prefix, string text)>();
        var samples = new List<Sample>();
        var log = new StringBuilder();

        // OCR is the slow part; its results are cached next to the output so template fitting can be re-run quickly.
        var cache = Path.ChangeExtension(outPath, ".samples.txt");
        // Images already OCR'd (with or without results) are listed in a ".done" file; only new images are OCR'd.
        var doneFile = cache + ".done";
        var done = new HashSet<string>();
        if (File.Exists(cache) && File.Exists(doneFile) && Environment.GetEnvironmentVariable("GLYPH_REOCR") != "1")
        {
            done = File.ReadAllLines(doneFile).ToHashSet();
            foreach (var l in File.ReadAllLines(cache))
            {
                var p = l.Split('|');
                int idx = paths.IndexOf(p[0]);
                if (idx >= 0) samples.Add(new Sample(idx, p[1], int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]), p[6] == "1"));
            }
        }

        for (int i = 0; i < paths.Count; i++)
        {
            if (done.Contains(paths[i])) continue;
            var f = Frame.FromFile(paths[i]);
            var seen = new List<(int x, int y)>();
            var name = Path.GetFileName(paths[i]);
            var verified = labels.FirstOrDefault(l => name.StartsWith(l.prefix));
            if (verified.text != null)
            {
                // Hand-verified text in this image: label the OCR line that resembles it (any pass), box = its leading words.
                Sample? bestS = null; int bestD = int.MaxValue;
                foreach (var mode in new[] { Preprocess.ColorText, Preprocess.BrightText, Preprocess.Raw })
                    foreach (var line in await ocr.ReadAsync(f, 3, mode))
                    {
                        var words = new List<OcrWord> { line.Words[0] };
                        foreach (var w in line.Words.Skip(1)) { if (w.X - words[^1].Right > Math.Max(4, words[^1].H * 0.8)) break; words.Add(w); }
                        var txt = string.Concat(words.Select(w => w.Text));
                        int d = Detection.NameTools.Levenshtein(txt.ToUpperInvariant(), verified.text);
                        if (d < bestD && d <= verified.text.Length / 2)
                        {
                            bestD = d;
                            int bx = words.Min(w => w.X), by = words.Min(w => w.Y);
                            bestS = new Sample(i, verified.text, bx, by, words.Max(w => w.Right) - bx, words.Max(w => w.Y + w.H) - by, true);
                        }
                    }
                if (bestS != null) samples.Add(bestS);
                continue;
            }
            foreach (var mode in new[] { Preprocess.BrightText, Preprocess.Raw, Preprocess.ColorText })
                foreach (var line in await ocr.ReadAsync(f, 2, mode))
                    foreach (var w in line.Words)
                    {
                        // Standalone numbers are HUD counters drawn in boxes (ammo, levels), not name text.
                        if (w.Text.All(char.IsAsciiDigit) && !line.Words.Any(o => o.Text.Count(char.IsAsciiLetter) >= 2)) continue;
                        var t = w.Text.Replace('\u00D8', '0').Replace('\u00F8', '0');
                        if (t.Length < (t.Any(char.IsAsciiDigit) ? 2 : 4) || t.Length > 24 || !t.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '#')) continue;
                        if (w.H < 6 || w.W < w.H) continue;
                        if (seen.Any(s => Math.Abs(s.x - w.X) < 6 && Math.Abs(s.y - w.Y) < 6)) continue;
                        seen.Add((w.X, w.Y));
                        samples.Add(new Sample(i, t, w.X, w.Y, w.W, w.H));
                    }
            if (i % 50 == 0) Console.WriteLine($"ocr {i}/{paths.Count}, {samples.Count} words");
        }
        File.WriteAllLines(cache, samples.Select(x => $"{paths[x.Image]}|{x.Label}|{x.X}|{x.Y}|{x.W}|{x.H}|{(x.Verified ? 1 : 0)}"));
        File.WriteAllLines(doneFile, paths);

        // Short letter+digit labels ("C200", "C320") are boxed HUD counters, not names.
        samples = samples.Where(x => x.Verified || !(x.Label.Length <= 4 && x.Label.Any(char.IsAsciiDigit) && x.Label.Any(char.IsAsciiLetter))).ToList();

        // Stable misreads ("PARKEPCHAFTHEIOIOT") are mapped to the majority spelling of the same word, then only labels
        // read in at least 3 frames are trusted.
        var raw = samples.Where(s => !s.Verified).GroupBy(s => s.Label).ToDictionary(g => g.Key, g => g.Count());
        var canon = new Dictionary<string, string>();
        foreach (var l in raw.Keys)
        {
            var m = raw.Where(kv => kv.Key.Length == l.Length && kv.Value > raw[l] && Detection.NameTools.Levenshtein(kv.Key, l) <= Math.Max(1, l.Length / 6))
                       .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            canon[l] = m ?? l;
        }
        // Follow chains (A -> B -> C).
        foreach (var l in canon.Keys.ToList()) { var c = canon[l]; for (int k = 0; k < 5 && canon[c] != c; k++) c = canon[c]; canon[l] = c; }
        samples = samples.Select(s => s.Verified ? s : s with { Label = canon[s.Label] }).ToList();
        var counts = samples.GroupBy(s => s.Label).ToDictionary(g => g.Key, g => g.Select(s => s.Image).Distinct().Count());
        samples = samples.Where(s => s.Verified || counts[s.Label] >= 3).GroupBy(s => s.Label).SelectMany(g => g.Take(s_cap(g.Key))).ToList();
        int s_cap(string label) => samples.Any(x => x.Verified && x.Label == label) ? 1000 : 30;
        log.AppendLine($"{samples.Count} trusted samples, {counts.Count(kv => kv.Value >= 3)} distinct words");
        foreach (var kv in counts.Where(kv => kv.Value >= 3).OrderByDescending(kv => kv.Value)) log.Append($"{kv.Key}x{kv.Value} ");
        log.AppendLine();

        var frames = new Dictionary<int, Frame>();
        Frame Img(int i) => frames.TryGetValue(i, out var f) ? f : frames[i] = Frame.FromFile(paths[i]);

        // Bright HUD text over a bright surround is either a boxed counter ("200" in a filled box) or text over snow;
        // both give polluted templates, so non-verified samples need a dark ring around the word.
        int before = samples.Count;
        samples = samples.Where(x => x.Verified || RingLuma(Img(x.Image), x) < 110).ToList();
        log.AppendLine($"ring filter: {before} -> {samples.Count}");

        // Round 0: naive grid straight from the OCR box (bright text).
        var reader = new GlyphReader();
        foreach (var s in samples)
        {
            var ink = GlyphReader.MakeInk(Img(s.Image), s.X - s.H, s.Y - s.H, s.W + 2 * s.H, s.H * 3, darkText: false);
            Accumulate(reader, ink, s.X, s.Y, s.W / (double)s.Label.Length, s.H, s.Label);
        }
        reader.Finish();
        reader.PitchRatio = Median(samples.Select(s => s.W / (double)s.Label.Length / s.H));
        log.AppendLine($"round 0: {reader.Templates.Count} chars, pitch ratio {reader.PitchRatio:0.000}");

        for (int round = 1; round <= 4; round++)
        {
            var next = new GlyphReader();
            var ratios = new List<double>();
            var scores = new List<double>();
            int kept = 0;
            foreach (var s in samples)
            {
                var f = Img(s.Image);
                (double score, double x0, double y, double pitch, double h, GlyphReader.Ink ink) best = (-1, 0, 0, 0, 0, null!);
                // Kill feed / squad names are bright text; only the verified prox samples (red on walls) can be dark.
                foreach (bool dark in s.Verified ? new[] { false, true } : new[] { false })
                {
                    var ink = GlyphReader.MakeInk(f, s.X - s.H, s.Y - s.H, s.W + 2 * s.H, s.H * 3, dark);
                    for (double hs = 0.85; hs <= 1.2001; hs += 0.05)
                    {
                        double h = s.H * hs;
                        for (double dy = -0.2; dy <= 0.2001; dy += 0.1)
                        {
                            double y = s.Y + (s.H - h) / 2 + dy * s.H;
                            double basePitch = s.W / (double)s.Label.Length;
                            for (double ps = 0.9; ps <= 1.1001; ps += 0.025)
                                for (double ph = -0.3; ph <= 0.3001; ph += 0.1)
                                {
                                    double pitch = basePitch * ps, x0 = s.X + ph * pitch;
                                    double sc = reader.ScoreLabel(ink, x0, y, pitch, h, s.Label);
                                    if (sc > best.score) best = (sc, x0, y, pitch, h, ink);
                                }
                        }
                    }
                }
                scores.Add(best.score);
                if (best.score < 0.55) continue;
                kept++;
                ratios.Add(best.pitch / best.h);
                Accumulate(next, best.ink, best.x0, best.y, best.pitch, best.h, s.Label);
            }
            next.Finish();
            next.PitchRatio = Median(ratios);
            reader = next;
            log.AppendLine($"round {round}: kept {kept}/{samples.Count}, median fit {Median(scores):0.000}, pitch ratio {reader.PitchRatio:0.000}, chars {string.Concat(reader.Templates.Keys.OrderBy(c => c))}");
        }

        // Characters never (or rarely) seen in the HUD are filled from the closest-matching font, aligned to the learned cells.
        if (Environment.GetEnvironmentVariable("GLYPH_FILL_FONT") is { Length: > 0 } fontPath && FontMatch.Render(fontPath) is { } font)
        {
            double fs = FontMatch.Match(font, reader, out var fit);
            log.AppendLine($"fill font {font.Name}: match {fs:0.000} fit {fit}");
            foreach (char c in FontMatch.Charset)
            {
                if (reader.Templates.TryGetValue(c, out var t) && t.Count >= 5) continue;
                var cell = Blur(font.Cell(c, fit.dx, fit.dy, fit.sx, fit.sy));
                reader.Templates[c] = new GlyphReader.Template { Char = c, Count = 1, Sum = cell };
                log.Append($"filled {c} ");
            }
            log.AppendLine();
            reader.Finish();
        }
        foreach (var t in reader.Templates.Values.OrderBy(t => t.Char)) log.Append($"{t.Char}:{t.Count} ");
        log.AppendLine();
        reader.Save(outPath);
        Atlas(reader).Save(atlasPath);
        File.WriteAllText(Path.ChangeExtension(outPath, ".log.txt"), log.ToString());
        Console.Write(log);
        return 0;
    }

    private static void Accumulate(GlyphReader r, GlyphReader.Ink ink, double x0, double y, double pitch, double h, string label)
    {
        for (int i = 0; i < label.Length; i++)
        {
            char c = label[i];
            if (c == ' ') continue;
            if (!r.Templates.TryGetValue(c, out var t)) r.Templates[c] = t = new GlyphReader.Template { Char = c };
            var cell = GlyphReader.Cell(ink, x0 + i * pitch, y, pitch, h);
            for (int j = 0; j < cell.Length; j++) t.Sum[j] += cell[j];
            t.Count++;
        }
    }

    /// <summary>Light 3x3 blur so crisp rendered glyphs look like the soft learned ones.</summary>
    private static float[] Blur(float[] a)
    {
        int W = GlyphReader.GW, H = GlyphReader.GH;
        var o = new float[a.Length];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double s = 0, wsum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        double w = dx == 0 && dy == 0 ? 4 : dx == 0 || dy == 0 ? 2 : 1;
                        s += a[yy * W + xx] * w; wsum += w;
                    }
                o[y * W + x] = (float)(s / wsum);
            }
        return o;
    }

    /// <summary>Median luminance of a band around the word box (outside it).</summary>
    private static double RingLuma(Frame f, Sample x)
    {
        int m = Math.Max(2, x.H / 2);
        var vals = new List<double>();
        for (int y = x.Y - m; y < x.Y + x.H + m; y++)
            for (int xx = x.X - m; xx < x.X + x.W + m; xx++)
            {
                bool inside = y >= x.Y && y < x.Y + x.H && xx >= x.X && xx < x.X + x.W;
                if (inside || xx < 0 || y < 0 || xx >= f.Width || y >= f.Height) continue;
                var (r, g, b) = f.Pixel(xx, y);
                vals.Add(0.299 * r + 0.587 * g + 0.114 * b);
            }
        return Median(vals);
    }

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToList();
        return a.Count == 0 ? 0 : a[a.Count / 2];
    }

    /// <summary>All templates side by side, 4x enlarged, for eyeballing.</summary>
    private static Frame Atlas(GlyphReader r)
    {
        var ts = r.Templates.Values.OrderBy(t => t.Char).ToList();
        int z = 4, cw = GlyphReader.GW * z + 4, chh = GlyphReader.GH * z + 4;
        int w = Math.Max(1, ts.Count) * cw, h = chh;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 60; px[i + 1] = 30; px[i + 2] = 30; px[i + 3] = 255; }
        for (int k = 0; k < ts.Count; k++)
        {
            var t = ts[k];
            float max = Math.Max(1e-3f, t.Sum.Max());
            for (int gy = 0; gy < GlyphReader.GH; gy++)
                for (int gx = 0; gx < GlyphReader.GW; gx++)
                {
                    byte v = (byte)Math.Clamp(t.Sum[gy * GlyphReader.GW + gx] / max * 255, 0, 255);
                    for (int yy = 0; yy < z; yy++)
                        for (int xx = 0; xx < z; xx++)
                        {
                            int X = k * cw + 2 + gx * z + xx, Y = 2 + gy * z + yy;
                            int o = (Y * w + X) * 4;
                            px[o] = px[o + 1] = px[o + 2] = v;
                        }
                }
        }
        return new Frame(w, h, px);
    }
}
