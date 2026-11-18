using SleazeDetector.Capture;
using SleazeDetector.Ocr;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: adds characters to the glyph templates from hand-labelled examples.
///   SleazeDetector --glyph-teach in.bin out.bin chars labels.txt
/// labels.txt has one example per line: "frame.png|x,y,w,h|TEXT" (the name's coloured ink box, frame pixels) or
/// "frame.png|x,y|TEXT" (a point on the name; its coloured run is found), and TEXT exactly what it says, one character per cell ("*****NAME69"). Each box is read with the
/// existing templates to fit the grid (pitch, phase, height); when the cell count equals the text length, the cells
/// of the characters listed in <c>chars</c> are averaged into those characters' templates.
/// </summary>
public static class GlyphTeach
{
    public static int Run(string[] args)
    {
        string inPath = args[0], outPath = args[1], chars = args[2], labels = args[3];
        GlyphReader g;
        using (var fs = File.OpenRead(inPath)) g = GlyphReader.Load(fs);
        int used = 0, skipped = 0;
        foreach (var line in File.ReadAllLines(labels).Where(l => l.Contains('|')))
        {
            var p = line.Split('|');
            var b = p[1].Split(',').Select(int.Parse).ToArray();
            string text = p[2];
            var f = Frame.FromFile(p[0]);
            if (b.Length == 2)
            {
                // A point on the name: use the coloured run there (its exact ink box).
                var run = Detection.HudRuns.Find(f, f.Height * 13.0 / 1200).SelectMany(r => r.Runs)
                    .FirstOrDefault(r => b[0] >= r.X0 - 2 && b[0] <= r.X1 + 2 && b[1] >= r.Y0 - 2 && b[1] <= r.Y1 + 2);
                if (run == null) { skipped++; continue; }
                b = new[] { run.X0, run.Y0, run.W, run.H };
            }
            var r = g.ReadRun(f, b[0], b[1], b[2], b[3], f.Height / 120.0);
            if (r == null || r.CellScores.Length != text.Length) { skipped++; continue; }
            // Guard against a different name at the labelled spot: the current reading must already agree with the
            // label on most of the characters that are not being taught.
            var raw = r.Raw.Length == text.Length ? r.Raw : r.Text.PadRight(text.Length);
            var known = Enumerable.Range(0, text.Length).Where(i => !chars.Contains(text[i]) && text[i] != ' ').ToList();
            if (known.Count > 0 && known.Count(i => Detection.NameTools.MatchKey(raw[i].ToString()) == Detection.NameTools.MatchKey(text[i].ToString())) < known.Count * 0.7)
            { skipped++; continue; }
            var ink = GlyphReader.MakeInk(f, b[0] - b[3], b[1] - b[3], b[2] + 2 * b[3], b[3] * 3, r.Dark);
            for (int i = 0; i < text.Length; i++)
            {
                if (!chars.Contains(text[i])) continue;
                if (!g.Templates.TryGetValue(text[i], out var t)) g.Templates[text[i]] = t = new GlyphReader.Template { Char = text[i] };
                var cell = GlyphReader.Cell(ink, r.X0 - f.ScreenX + i * r.Pitch, r.Y0 - f.ScreenY, r.Pitch, r.H);
                for (int k = 0; k < cell.Length; k++) t.Sum[k] += cell[k];
                t.Count++;
            }
            used++;
        }
        g.Finish();
        g.Save(outPath);
        Console.WriteLine($"examples used {used}, skipped {skipped} (cell count did not match the text)");
        foreach (var c in chars) Console.WriteLine($"'{c}': {(g.Templates.TryGetValue(c, out var t) ? t.Count : 0)} samples");
        return 0;
    }
}
