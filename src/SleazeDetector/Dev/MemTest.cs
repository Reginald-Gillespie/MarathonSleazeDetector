using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using SleazeDetector.Capture;
using SleazeDetector.Core;
using SleazeDetector.Detection;
using SleazeDetector.Ocr;
using SleazeDetector.Reputation;
using SleazeDetector.Session;
using SleazeDetector.UI;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: runs the live capture and reading loop against the desktop (the primary screen stands in for the game)
/// and logs memory every two seconds.
///   SleazeDetector --mem-test out.txt [seconds]
/// SLEAZE_MEM_OCR=1 adds the Windows OCR scanner, SLEAZE_MEM_NET=1 reads 20 extra boxes per tick with the name model,
/// SLEAZE_MEM_UI=1 shows the overlay with four meters.
/// Columns: time, working set, private bytes, managed heap, large object heap, gen0/1/2 collections.
/// </summary>
public static class MemTest
{
    public static int Run(string[] args)
    {
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 60;
        var settings = new AppSettings();
        var tracker = new RunTracker();
        var fast = new FastHud(GlyphReader.LoadEmbedded(), tracker, () => settings) { Net = NameNet.Load() };
        if (fast.Net == null) fast.Font = FontReader.Load();
        var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var game = new GameWindow(IntPtr.Zero, b.X, b.Y, b.Width, b.Height, true);
        var scanner = Environment.GetEnvironmentVariable("SLEAZE_MEM_OCR") == "1" ? new Scanner(new HudOcr(), tracker, () => settings) : null;
        var net = Environment.GetEnvironmentVariable("SLEAZE_MEM_NET") == "1" ? fast.Net : null;
        bool ui = Environment.GetEnvironmentVariable("SLEAZE_MEM_UI") == "1";
        var rng = new Random(1);
        var slow = settings.Zones.Where(z => z.Name is not (AppSettings.FeedZone or AppSettings.ProxZone)).ToList();
        var sb = new StringBuilder("t_s\tworking_mb\tprivate_mb\theap_mb\tloh_mb\tgc0\tgc1\tgc2\n");

        OverlayWindow? overlay = null;
        var badges = new List<BadgeRequest>();
        if (ui)
        {
            overlay = new OverlayWindow(new ReputationService(() => settings), () => settings);
            overlay.Show();
            overlay.Track(game);
            int h = (int)(13.0 / 1200 * b.Height);
            for (int i = 0; i < 4; i++)
                badges.Add(new BadgeRequest(Guid.NewGuid(), $"RUNNER{i}", AppSettings.FeedZone, b.Width / 4, b.Height * 3 / 4 + i * h * 2, h));
        }

        void Loop()
        {
            Task? ocr = null;
            var nextSlow = DateTime.Now;
            var proc = Process.GetCurrentProcess();
            var sw = Stopwatch.StartNew();
            var nextLog = TimeSpan.Zero;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                tracker.HudSeen();
                fast.Tick(game, Frame.CaptureScreen);
                if (net != null)
                {
                    var f = Frame.CaptureScreen(0, b.Height / 2, b.Width / 2, b.Height / 3);
                    for (int i = 0; i < 20; i++)
                        net.Read(f, rng.Next(0, 200), f.ScreenY + rng.Next(0, 300), rng.Next(30, 400), rng.Next(12, 22), b.Height);
                }
                if (scanner != null) { if (ocr == null || ocr.IsCompleted) ocr = Task.Run(() => scanner.TickAsync(game, Frame.CaptureScreen)); }
                else if (DateTime.Now >= nextSlow)
                {
                    // Without the scanner the slow zones are still grabbed every 400 ms, as the scanner does.
                    foreach (var z in slow)
                        Frame.CaptureScreen((int)(z.X * b.Width), (int)(z.Y * b.Height), (int)(z.W * b.Width), (int)(z.H * b.Height));
                    nextSlow = DateTime.Now.AddMilliseconds(400);
                }
                overlay?.Dispatcher.Invoke(() => { overlay.Track(game); overlay.SetBadges(badges); });
                if (sw.Elapsed >= nextLog)
                {
                    proc.Refresh();
                    var gc = GC.GetGCMemoryInfo();
                    double loh = gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes / 1e6 : 0;
                    sb.AppendLine($"{sw.Elapsed.TotalSeconds:0}\t{proc.WorkingSet64 / 1e6:0}\t{proc.PrivateMemorySize64 / 1e6:0}\t{GC.GetTotalMemory(false) / 1e6:0}\t{loh:0}\t{GC.CollectionCount(0)}\t{GC.CollectionCount(1)}\t{GC.CollectionCount(2)}");
                    nextLog += TimeSpan.FromSeconds(2);
                }
                Thread.Sleep(30);
            }
        }

        if (overlay != null)
        {
            // The overlay needs the UI thread to keep rendering: run the loop beside it.
            var frame = new DispatcherFrame();
            Task.Run(() => { try { Loop(); } finally { frame.Continue = false; } });
            Dispatcher.PushFrame(frame);
            overlay.Close();
        }
        else Loop();
        File.WriteAllText(args[0], sb.ToString());
        return 0;
    }
}
