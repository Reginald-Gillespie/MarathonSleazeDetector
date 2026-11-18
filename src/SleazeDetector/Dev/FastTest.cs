using System.Diagnostics;
using System.Text;
using SleazeDetector.Capture;
using SleazeDetector.Core;
using SleazeDetector.Detection;
using SleazeDetector.Ocr;
using SleazeDetector.Session;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: runs the per-frame kill feed / prox path over video frames as if they were live, one frame per tick.
///   SleazeDetector --fast-test outdir frame1.png frame2.png ...
/// Writes report.txt (meters, names and time per frame) and, for frames with anything detected, an annotated crop
/// of the left side of the screen: name boxes (red / lime outline) and where each meter would be drawn (white box).
/// </summary>
public static class FastTest
{
    public static int Run(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);
        var paths = args.Skip(1).SelectMany<string, string>(a => Directory.Exists(a) ? Directory.GetFiles(a, "*.png").OrderBy(f => f) : new[] { a }).ToList();
        var settings = new AppSettings();
        var tracker = new RunTracker();
        var clock = DateTime.Now;
        double fps = double.TryParse(Environment.GetEnvironmentVariable("FAST_TEST_FPS"), out var v) ? v : 4;
        var sb = new StringBuilder();
        bool trace = Environment.GetEnvironmentVariable("FAST_TEST_TRACE") is "1" or "2";
        if (Environment.GetEnvironmentVariable("FAST_TEST_TRACE") == "2") FontReader.Debug = line => sb.AppendLine(line);
        var fast = new FastHud(GlyphReader.LoadEmbedded(), tracker, () => settings)
        {
            Now = () => clock,
            Trace = trace ? line => sb.AppendLine(line) : null,
        };
        if (Environment.GetEnvironmentVariable("SLEAZE_NO_FONT") != "1") fast.Font = FontReader.Load();
        // SLEAZE_NET=path to an .onnx model, "1" for the embedded one, unset or "0" for none.
        if (Environment.GetEnvironmentVariable("SLEAZE_NET") is { Length: > 0 } nm && nm != "0") fast.Net = NameNet.Load(nm == "1" ? null : nm);
        var sw = new Stopwatch();
        // FAST_TEST_REALTIME=1: behave like the live loop: when a tick takes longer than a frame, frames that played
        // meanwhile are skipped (live always grabs the newest), and the clock follows the real time spent (+30 ms delay).
        bool realtime = Environment.GetEnvironmentVariable("FAST_TEST_REALTIME") == "1";
        // A live recording (FrameRecorder) carries its own tick times: replay with those.
        var times = new Dictionary<string, double>();
        foreach (var dir in args.Skip(1).Where(Directory.Exists))
        {
            var tf = Path.Combine(dir, "times.txt");
            if (File.Exists(tf))
                foreach (var line in File.ReadAllLines(tf).Select(l => l.Split(' ')).Where(p => p.Length == 2))
                    times[Path.Combine(dir, line[0])] = double.Parse(line[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        var start = clock;
        double videoTime = 0;
        int lastIdx = -1;
        for (int i = 0; i < paths.Count; i++)
        {
            if (realtime)
            {
                int idx = (int)(videoTime * fps);
                if (idx >= paths.Count) break;
                if (idx <= lastIdx) idx = lastIdx + 1;
                i = idx; lastIdx = idx;
                clock = start.AddSeconds(videoTime);
            }
            else clock = times.TryGetValue(paths[i], out var ms) ? start.AddMilliseconds(ms) : clock.AddSeconds(1 / fps);
            var path = paths[i];
            var img = Frame.FromFile(path);
            var game = new GameWindow(IntPtr.Zero, 0, 0, img.Width, img.Height, true);
            sw.Restart();
            tracker.HudSeen();   // frames are taken to be from a match (the OCR path that detects one does not run here)
            var badges = fast.Tick(game, (x, y, w, h) => img.Crop(x, y, w, h));
            sw.Stop();
            if (realtime) videoTime += Math.Max(1 / fps, sw.Elapsed.TotalSeconds + 0.03);
            var names = fast.Visible.Select(v => $"{v.Name ?? "?"}[{v.Color}]{(v.Confirmed ? "" : "(unconfirmed)")}<{v.Run.W}x{v.Run.H}>");
            sb.AppendLine($"{Path.GetFileName(path)} {sw.ElapsedMilliseconds,4}ms names: {string.Join(", ", names)} | meters: {string.Join(", ", badges.Select(b => $"{b.Key}@{b.X},{b.Y}h{b.H}"))}");
            if (fast.Visible.Count > 0) Annotate(img, fast.Visible, badges).Save(Path.Combine(outDir, Path.GetFileName(path)));
        }
        File.WriteAllText(Path.Combine(outDir, "report.txt"), sb.ToString());
        return 0;
    }

    private static Frame Annotate(Frame img, IReadOnlyList<FastHud.VisibleName> vis, List<BadgeRequest> badges)
    {
        int cw = Math.Min(img.Width, img.Width * 45 / 100);
        var f = img.Crop(0, 0, cw, img.Height);
        foreach (var v in vis)
            Box(f, v.Run.X0 - 1, v.Run.Y0 - 1, v.Run.X1 + 1, v.Run.Y1 + 1, v.Color == TextColor.Red ? (255, 60, 60) : (120, 255, 60));
        foreach (var b in badges)
        {
            int x0 = b.X + Math.Max(3, (int)(b.H * 0.6)), y0 = b.Y - (int)(b.H * 0.15);
            Box(f, x0, y0, x0 + b.H * 4, y0 + (int)(b.H * 1.3), (255, 255, 255));
        }
        return f;
    }

    private static void Box(Frame f, int x0, int y0, int x1, int y1, (int r, int g, int b) c)
    {
        void P(int x, int y)
        {
            if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) return;
            int o = (y * f.Width + x) * 4;
            f.Bgra[o] = (byte)c.b; f.Bgra[o + 1] = (byte)c.g; f.Bgra[o + 2] = (byte)c.r;
        }
        for (int x = x0; x <= x1; x++) { P(x, y0); P(x, y1); }
        for (int y = y0; y <= y1; y++) { P(x0, y); P(x1, y); }
    }
}
