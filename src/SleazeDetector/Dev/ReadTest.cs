using System.Diagnostics;
using System.Text;
using SleazeDetector.Capture;
using SleazeDetector.Ocr;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: reading accuracy on a labelled set of name boxes.
///   SleazeDetector --read-test corpus.txt report.txt [old]
/// Each corpus line: image path, x, y, w, h, zone (feed / prox), true text (tab separated), and optionally the game
/// height the image came from (for crops rather than whole frames). Reports every miss, the
/// exact-match rate per true name, character accuracy and time per read. "old" uses the learned glyph templates.
/// </summary>
public static class ReadTest
{
    public static int Run(string[] args)
    {
        var lines = File.ReadAllLines(args[0]).Select(l => l.Split('\t')).Where(p => p.Length >= 7).ToList();
        bool old = args.Length > 2 && args[2] == "old";
        // "net [model.onnx]": the learned reader (the embedded model unless a file is given).
        var net = args.Length > 2 && args[2] == "net" ? NameNet.Load(args.Length > 3 ? args[3] : null) : null;
        var font = old || net != null ? null : FontReader.Load();
        var dbg = new StringBuilder();
        if (Environment.GetEnvironmentVariable("READ_TEST_DEBUG") == "1") FontReader.Debug = l => dbg.AppendLine(l);
        var glyphs = GlyphReader.LoadEmbedded();
        var sb = new StringBuilder();
        // READ_TEST_SCORES=file: one line per read (truth, read, score, lowest character score) for threshold tuning.
        var scoresPath = Environment.GetEnvironmentVariable("READ_TEST_SCORES");
        var scores = string.IsNullOrEmpty(scoresPath) ? null : new StringBuilder();
        var frames = new Dictionary<string, Frame>();
        int exact = 0, chars = 0, charOk = 0;
        var perName = new Dictionary<string, (int n, int ok)>();
        var sw = new Stopwatch();
        double totalMs = 0, maxMs = 0;
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
        foreach (var p in lines)
        {
            string path = Path.IsPathRooted(p[0]) ? p[0] : Path.Combine(baseDir, p[0]);
            if (!frames.TryGetValue(path, out var f))
            {
                if (frames.Count > 40) frames.Clear();
                frames[path] = f = Frame.FromFile(path);
            }
            int x = int.Parse(p[1]), y = int.Parse(p[2]), w = int.Parse(p[3]), h = int.Parse(p[4]);
            bool prox = p[5] == "prox";
            string truth = p[6];
            if (Environment.GetEnvironmentVariable("READ_TEST_DUMP") is { Length: > 0 } dd)
                FontReader.DumpPath = Path.Combine(dd, $"{lines.IndexOf(p):000}_{truth.Replace('*', 'x').Replace('#', 'h')}.png");
            FontReader.DebugTruth = truth;
            sw.Restart();
            var r = net != null
                ? (net.Read(f, x, y, w, h, p.Length > 7 ? int.Parse(p[7]) : f.Height) is { } nr ? new GlyphReader.Result(nr.Text, nr.Score, nr.CharConf, 0, 0, 0, 0) : null)
                : font != null
                ? font.ReadRun(f, x, y, w, h, (prox ? 9.3 : 10.3) / 1200 * f.Height, tagged: prox)
                : glyphs.ReadRun(f, x, y, w, h, 10.0 / 1200 * f.Height);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            totalMs += ms; maxMs = Math.Max(maxMs, ms);
            string got = r?.Text.Trim() ?? "";
            bool ok = got == truth;
            if (ok) exact++;
            var (n0, ok0) = perName.GetValueOrDefault(truth);
            perName[truth] = (n0 + 1, ok0 + (ok ? 1 : 0));
            int d = Lev(got, truth);
            chars += truth.Length; charOk += Math.Max(0, truth.Length - d);
            scores?.AppendLine($"{truth}	{got}	{r?.Score ?? 0:0.000}	{(r?.CellScores.Length > 0 ? r.CellScores.Min() : 0):0.000}");
            if (!ok) sb.AppendLine($"MISS {p[0]} {x},{y} {w}x{h}: '{got}' want '{truth}' score={r?.Score:0.00} {ms:0}ms");
        }
        var head = new StringBuilder();
        head.AppendLine($"exact {exact}/{lines.Count} = {100.0 * exact / Math.Max(1, lines.Count):0.0}%   chars {100.0 * charOk / Math.Max(1, chars):0.0}%   avg {totalMs / Math.Max(1, lines.Count):0}ms max {maxMs:0}ms  templates {FontReader.Templates} qualities {FontReader.Qualities} banks {FontReader.BanksMade} known {FontReader.KnownReads}/{FontReader.Reads}");
        foreach (var (name, (n, okN)) in perName.OrderBy(kv => kv.Key)) head.AppendLine($"  {okN,4}/{n,-4} {name}");
        File.WriteAllText(args[1], head.ToString() + sb + dbg);
        if (scores != null) File.WriteAllText(scoresPath!, scores.ToString());
        return 0;
    }

    private static int Lev(string a, string b)
    {
        var d = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0]; d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }
}
