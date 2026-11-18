using System.Diagnostics;
using System.Globalization;
using SleazeDetector.Capture;
using SleazeDetector.Core;
using SleazeDetector.Detection;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tools for shell recognition (needs ffmpeg on PATH; clips are treated as 16:10 and scaled to 1920x1200).
///   --learn-shell name video seedSecond [shells.bin]
///       Learns the ability icon templates of a shell from a recording of it: starts from the slots at seedSecond
///       (pick a moment with the ability bar clearly visible), then averages every sampled second that matches,
///       twice, so all slot states (ready / active / recharging / tinted) shape the template. Adds or replaces the
///       shell in shells.bin (default: Assets\shells.bin next to the project).
///   --shell-test video [video ...]
///       Scores one frame every two seconds of each video and prints the score spread, matches and the verdict.
/// </summary>
public static class ShellLearn
{
    // SHELL_TEST_HEIGHT: test at another game size (e.g. 1800 for 2880x1800) to exercise the resampling.
    private static readonly int TestH = int.TryParse(Environment.GetEnvironmentVariable("SHELL_TEST_HEIGHT"), out var th) ? th : 1200;
    private static readonly GameWindow Game = new(IntPtr.Zero, 0, 0, 1920, 1200, true);
    private static GameWindow TestGame => new(IntPtr.Zero, 0, 0, TestH * 8 / 5, TestH, true);

    private static IEnumerable<Frame> Frames(string video, double fps, GameWindow? game = null)
    {
        var g = game ?? Game;
        var (x, y, w, h) = ShellDetector.SampleBox(g);
        var psi = new ProcessStartInfo("ffmpeg",
            $"-loglevel error -i \"{video}\" -vf \"fps={fps.ToString(CultureInfo.InvariantCulture)},scale={g.Width}:{g.Height}:flags=bicubic,crop={w}:{h}:{x}:{y}\" -f rawvideo -pix_fmt bgra -")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var stream = p.StandardOutput.BaseStream;
        int size = w * h * 4;
        while (true)
        {
            var buf = new byte[size];
            int got = 0;
            while (got < size) { int n = stream.Read(buf, got, size - got); if (n <= 0) break; got += n; }
            if (got < size) break;
            yield return new Frame(w, h, buf, x, y);
        }
        p.WaitForExit();
    }

    private static float[,] Grad(Frame f) => ShellDetector.Sample(Game, (_, _, _, _) => f);

    public static int Learn(string[] args)
    {
        string name = args[0], video = args[1];
        int seed = int.Parse(args[2]);
        string outFile = args.Length > 3 ? args[3] : Path.Combine(AppContext.BaseDirectory, "shells.bin");
        var grads = Frames(video, 1).Select(Grad).ToList();
        Console.WriteLine($"{grads.Count} frames");
        var tmpl = new Dictionary<string, (int W, int H, float[] V)>();
        foreach (var slot in ShellDetector.SlotNames)
        {
            var (w, h) = ShellDetector.PatchSize(slot);
            int x0 = ShellDetector.SlotX0(slot), sh = ShellDetector.ShiftRange;
            var t = ShellDetector.Patch(grads[seed], x0, 0, 0, w, h);
            for (int iter = 0; iter < 2; iter++)
            {
                var sum = new double[w * h]; int used = 0;
                foreach (var g in grads)
                {
                    float[]? best = null; double bs = 0.5;
                    for (int dy = -sh; dy <= sh; dy++)
                        for (int dx = -sh; dx <= sh; dx++)
                        {
                            var p = ShellDetector.Patch(g, x0, dx, dy, w, h);
                            double s = Ncc(p, t);
                            if (s > bs) { bs = s; best = p; }
                        }
                    if (best == null) continue;
                    for (int i = 0; i < sum.Length; i++) sum[i] += best[i];
                    used++;
                }
                if (used == 0) { Console.WriteLine($"slot {slot}: nothing matched the seed frame"); return 1; }
                t = sum.Select(v => (float)(v / used)).ToArray();
                Console.WriteLine($"slot {slot} pass {iter + 1}: {used} frames");
            }
            tmpl[slot] = (w, h, t);
        }
        var shells = File.Exists(outFile) ? ShellDetector.Load(File.OpenRead(outFile)) : new();
        shells.RemoveAll(s => s.Name == name);
        shells.Add(new ShellDetector.ShellTemplate { Name = name, Slots = tmpl });
        using (var fs = File.Create(outFile)) ShellDetector.Save(fs, shells);
        Console.WriteLine($"saved {name} to {outFile}");
        return 0;
    }

    public static int Test(string[] args)
    {
        var det = new ShellDetector(ShellDetector.LoadEmbedded());
        if (!det.IsReady) { Console.WriteLine("no shell templates"); return 1; }
        foreach (var video in args)
        {
            det.Reset();
            var scores = new List<double>();
            int hits = 0;
            var tg = TestGame;
            foreach (var f in Frames(video, 0.5, tg))
            {
                var (shell, score) = det.Classify(ShellDetector.Sample(tg, (_, _, _, _) => f));
                scores.Add(score);
                if (shell != null) hits++;
                det.Observe(tg, (_, _, _, _) => f);
            }
            scores.Sort();
            double P(double q) => scores.Count == 0 ? 0 : scores[(int)Math.Min(scores.Count - 1, q * scores.Count)];
            Console.WriteLine($"{Path.GetFileName(video)}: n={scores.Count} p50={P(0.5):0.00} p90={P(0.9):0.00} max={P(1):0.00} matches={hits} -> {det.Current ?? "unknown / other"}");
        }
        return 0;
    }

    private static double Ncc(float[] a, float[] b)
    {
        double ma = a.Average(), mb = b.Average(), ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { double x = a[i] - ma, y = b[i] - mb; ab += x * y; aa += x * x; bb += y * y; }
        return aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 0;
    }
}
