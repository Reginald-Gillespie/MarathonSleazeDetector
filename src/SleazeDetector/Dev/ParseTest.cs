using System.Text;
using SleazeDetector.Capture;
using SleazeDetector.Core;
using SleazeDetector.Detection;
using SleazeDetector.Ocr;
using SleazeDetector.Session;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: runs the full detection pipeline over screenshots / extracted video frames as if they were the live game.
///   SleazeDetector --parse-test out.txt frame1.png frame2.png ...
/// </summary>
public static class ParseTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var sb = new StringBuilder();
        var settings = new AppSettings();
        var tracker = new RunTracker();
        var scanner = new Scanner(new HudOcr(), tracker, () => settings);
        var ended = new List<RunSession>();
        tracker.RunEnded += r => { sb.AppendLine($"  >> RUN ENDED ({r.EndReason})"); ended.Add(r); };
        tracker.RunnerConfirmed += r => sb.AppendLine($"  >> confirmed {r.Name} [{r.Relation}] via {r.Sources}");
        List<BadgeRequest> last = new();
        scanner.Badges += b => last = b;

        foreach (var path in args.Skip(1))
        {
            var img = Frame.FromFile(path);
            last = new();
            var game = new GameWindow(IntPtr.Zero, 0, 0, img.Width, img.Height, true);
            await scanner.TickAsync(game, (x, y, w, h) => img.Crop(x, y, w, h), force: true);
            sb.AppendLine($"{Path.GetFileName(path)}: self={tracker.SelfName ?? "-"} badges=[{string.Join(", ", last.Select(b => $"{b.Key}@{b.X},{b.Y}"))}]");
        }

        void Dump(RunSession? run, string label)
        {
            if (run == null) return;
            sb.AppendLine($"== {label} run {run.RunId} ended={run.EndReason}");
            foreach (var r in run.Runners.OrderByDescending(r => r.Sightings))
                sb.AppendLine($"   {(r.Confirmed ? "*" : " ")} {r.Name,-36} key={r.Key,-36} {r.Relation,-9} {r.Sources} x{r.Sightings} | {string.Join("; ", r.Notes)} | tags: {string.Join(" ", r.TagReads.Select(kv => $"{kv.Key}x{kv.Value:0.00}"))}");
        }
        foreach (var r in ended) Dump(r, "ended");
        Dump(tracker.Current, "current");
        File.WriteAllText(args[0], sb.ToString());
        return 0;
    }
}
