using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SleazeDetector.Core;
using SleazeDetector.Detection;
using SleazeDetector.Reputation;
using SleazeDetector.Session;
using SleazeDetector.UI;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: renders the review window and sample meter badges to a PNG without showing anything on screen.
///   SleazeDetector --ui-preview out.png
/// </summary>
public static class UiPreview
{
    private static readonly string[] ManyNames =
    {
        "QUARTZWOLF", "MOSSHOLLOW", "BRINEDIVER", "COPPERJAY", "TALLGRASS", "NIGHTKETTLE", "PEBBLEHOUND",
        "SALTMARSH", "IRONLOTUS", "GLASSHERON", "DUSKVIOLET", "EMBERFINCH", "RIVERSLATE", "VELVETCROW",
    };

    /// <summary>SLEAZE_PREVIEW_SHOW=1: really shows the window (empty, as from the tray outside a run; then a list that
    /// grows while open; then a review; then empty again) and writes its screen rectangle in physical pixels to the out
    /// file. Each step is sampled ten times; a window that keeps moving or resizing lists more than one rectangle.</summary>
    private static int ShowTest(ReputationService rep, string outFile)
    {
        var lines = new List<string>();
        var win = new RunWindow(rep);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Any hide or show of the window, with what asked for it (to find a hide that the test did not make).
        win.SourceInitialized += (_, _) =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(win).Handle).AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                uint flags = msg == 0x46 ? (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lp, IntPtr.Size * 2 + 16) : 0;
                if (msg == 0x18 || (msg == 0x46 && (flags & 0xC0) != 0) || msg == 0x47 && (System.Runtime.InteropServices.Marshal.ReadInt32(lp, IntPtr.Size * 2 + 16) & 0x80) != 0)
                    lines.Add($"    {clock.ElapsedMilliseconds} ms msg 0x{msg:X} wp={wp} flags=0x{flags:X} from: " +
                        string.Join(" < ", Environment.StackTrace.Split('\n').Select(l => l.Trim())
                            .Where(l => l.Contains("SleazeDetector.") && !l.Contains("ShowTest>b__") || l.Contains("ShowWindow") || l.Contains("SetWindowPos")).Take(6)));
                return IntPtr.Zero;
            });
        void Wait(int ms)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
            t.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        void Report(string what)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            var seen = new List<string>();
            for (int k = 0; k < 60; k++)
            {
                Wait(15);
                Native.Win32.GetWindowRect(hwnd, out var r);
                string v = $"{r.Left},{r.Top} {r.Width}x{r.Height} visible={Native.Win32.IsWindowVisible(hwnd)}";
                if (!seen.Contains(v)) seen.Add(v);
            }
            var m = Native.Win32.MonitorOf(IntPtr.Zero);
            lines.Add($"{clock.ElapsedMilliseconds} ms {what}: {string.Join(" | ", seen)}  (monitor work {m?.Work.Left},{m?.Work.Top} {m?.Work.Width}x{m?.Work.Height})");
        }
        win.ShowFor(null, RunWindow.Mode.Roster, activate: false);
        Wait(300);
        Report("empty");
        win.Hide();
        var tracker = new RunTracker();
        tracker.HudSeen();
        win.ShowFor(tracker.Current, RunWindow.Mode.Roster, activate: false);
        Wait(300);
        Report("run, nobody yet");
        foreach (var n in ManyNames)
        {
            for (int i = 0; i < 3; i++) tracker.Add(new Sighting(n, 0, 0, 10, 10, SightSource.KillFeed, Relation.Enemy, null), 2);
            win.Refresh();
            Wait(60);
        }
        Report("grew while open");
        tracker.EndSignal("exfiltrated");
        win.ShowFor(tracker.Current ?? win.Run, RunWindow.Mode.Review, activate: false);
        Report("review");
        // The list shrinks while the window is open: the window keeps its height.
        win.ShowFor(null, RunWindow.Mode.Roster, activate: false);
        Report("emptied while open (height should stay)");
        win.Hide();
        // A short list that grows a little (well under the limit) and shrinks back while open.
        var small = new RunTracker();
        small.HudSeen();
        foreach (var n in ManyNames.Take(2))
            for (int i = 0; i < 3; i++) small.Add(new Sighting(n, 0, 0, 10, 10, SightSource.KillFeed, Relation.Enemy, null), 2);
        win.ShowFor(small.Current, RunWindow.Mode.Roster, activate: false);
        Wait(300);
        Report("two runners");
        foreach (var n in ManyNames.Skip(2).Take(2))
            for (int i = 0; i < 3; i++) small.Add(new Sighting(n, 0, 0, 10, 10, SightSource.KillFeed, Relation.Enemy, null), 2);
        win.Refresh();
        Report("four runners (grows down, stays clear of the bottom margin)");
        win.ShowFor(null, RunWindow.Mode.Roster, activate: false);
        Report("emptied while open (height should stay)");
        win.Hide();
        win.ShowFor(null, RunWindow.Mode.Roster, activate: false);
        Wait(300);
        Report("empty again");
        win.Hide();
        File.WriteAllLines(outFile, lines);
        return 0;
    }

    /// <summary>SLEAZE_PREVIEW_RATENAME="name": how the rate-by-name window takes a few typed names (written to
    /// out.txt) and a picture of it after typing the given one (out.png).</summary>
    private static int RateNameTest(ReputationService rep, string typed, string outFile)
    {
        var lines = new List<string>();
        foreach (var t in new[] { "quartzwolf", "QuartzWolf#0816", "quartzwolf # 0816", "QW#12", "ab#1234", "a", "", "MOSS HOLLOW", "BRINE-DIVER", typed })
            lines.Add($"'{t}' -> {RateByNameWindow.Problem(t, out var n) ?? "ok"} | {n} | key {NameTools.RunnerKey(n)}");
        foreach (var t in new[] { "UESC RECRUIT", "UESCIRECRUIT", "UESC GRENAOIER", "USEC GHOST CMDR", "UESC WRAITH WARDEN",
                                  "UESC FAN", "UESC", "UESCLORD", "QUARTZWOLF", "RECRUIT", "GHOSTLY", "UESC TROOPERS" })
            lines.Add($"bot check '{t}' -> {(NameTools.CleanName(NameTools.Words(t), hudFilter: false) is { } n ? "runner " + n : "not a runner")}");
        File.WriteAllLines(Path.ChangeExtension(outFile, ".txt"), lines);
        var win = new RateByNameWindow(rep);
        var box = (TextBox)typeof(RateByNameWindow).GetField("_name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(win)!;
        box.Text = typed;
        typeof(RateByNameWindow).GetMethod("Lookup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(win, null);
        var host = (FrameworkElement)win.Content;
        win.Content = null;
        var root = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x40)) };
        root.Children.Add(host);
        root.Width = 584;
        root.Measure(new Size(584, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outFile)) enc.Save(fs);
        return 0;
    }

    public static int Run(string[] args)
    {
        var settings = new AppSettings();
        var rep = new ReputationService(() => settings);
        if (Environment.GetEnvironmentVariable("SLEAZE_PREVIEW_SHOW") == "1") return ShowTest(rep, args[0]);
        if (Environment.GetEnvironmentVariable("SLEAZE_PREVIEW_RATENAME") is { Length: > 0 } typed) return RateNameTest(rep, typed, args[0]);
        var tracker = new RunTracker();
        void Seen(string name, SightSource src, Relation rel, string? note, int n = 3)
        {
            for (int i = 0; i < n; i++) tracker.Add(new Sighting(name, 0, 0, 10, 10, src, rel, note), 2);
        }
        tracker.HudSeen();   // start a run
        Seen("SQUADMATE_ONE", SightSource.Squad, Relation.Teammate, null);
        Seen("PROXTALKER#1234", SightSource.Prox, Relation.Enemy, "Talked in proximity chat");
        Seen("FEEDNAME1833", SightSource.KillFeed, Relation.Enemy, "Killed SQUADMATE_ONE");
        // SLEAZE_PREVIEW_MANY=1: a long list, laid out at the window's height limit (shows the scrolling).
        bool many = Environment.GetEnvironmentVariable("SLEAZE_PREVIEW_MANY") == "1";
        if (many)
            foreach (var n in ManyNames)
                Seen(n, SightSource.KillFeed, Relation.Enemy, null);
        var run = tracker.Current!;

        var win = new RunWindow(rep);
        win.Prepare(run, RunWindow.Mode.Review);
        // (The expanded "bad experience" state is not simulated: clicking would file a real report.)
        var cards = FindAll<RunnerCard>((DependencyObject)win.Content).ToList();

        var root = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x40)) };
        var host = (FrameworkElement)win.Content;
        win.Content = null;
        root.Children.Add(host);

        var badges = new WrapPanel { Margin = new Thickness(20, 0, 20, 20) };
        var samples = new (string label, RepSummary? rep)[]
        {
            ("unrated", null),
            ("1 bad", Summary(settings, 0, 1)),
            ("2 bad", Summary(settings, 0, 2)),
            ("3 bad", Summary(settings, 0, 3)),
            ("4 bad", Summary(settings, 0, 4)),
            ("5 bad", Summary(settings, 0, 5)),
            ("6 bad", Summary(settings, 0, 6)),
            ("2 good 1 bad", Summary(settings, 2, 1)),
            ("1 good", Summary(settings, 1, 0)),
            ("3 good", Summary(settings, 3, 0)),
            ("5 good", Summary(settings, 5, 0)),
            ("you: backstab", Summary(settings, 0, 2, mine: Verdicts.Bad, backstab: true)),
            ("you: kills rooks", Summary(settings, 0, 1, mine: Verdicts.Bad, rook: true)),
        };
        GameAssets.LoadRookKillerIconAsync().GetAwaiter().GetResult();
        // The tray icon as Windows will get it, next to the preview.
        using (var tray = GameAssets.ToTrayIcon(GameAssets.RookKillerIcon))
            tray?.ToBitmap().Save(Path.ChangeExtension(args[0], ".tray.png"), System.Drawing.Imaging.ImageFormat.Png);
        foreach (var (label, r) in samples)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 18, 0) };
            sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontFamily = new FontFamily("Consolas"), Foreground = Brushes.LightGreen, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var m = new TrustMeter(); m.Set(r); sp.Children.Add(m);
            badges.Children.Add(sp);
        }
        root.Children.Add(badges);

        if (many) { host.Height = win.MaxHeight; root.Children.Remove(badges); }
        root.Measure(new Size(600, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(args[0])) enc.Save(fs);

        // Optional second file: the settings window.
        if (args.Length > 1)
        {
            var sw = new SettingsWindow(settings, devMode: false, new Reputation.ReputationService(() => settings));
            var content = (FrameworkElement)sw.Content;
            sw.Content = null;
            var panel = new Border { Background = sw.Background, Width = sw.Width, Child = content };
            panel.Measure(new Size(sw.Width, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            panel.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(panel);
            var e2 = new PngBitmapEncoder();
            e2.Frames.Add(BitmapFrame.Create(bmp));
            using var f2 = File.Create(args[1]);
            e2.Save(f2);
        }
        return 0;
    }

    private static RepSummary Summary(AppSettings s, int good, int bad, string? mine = null, bool backstab = false, bool rook = false) => new()
    {
        Good = good, Bad = bad, WeightedGood = good, WeightedBad = bad, Backstab = backstab ? 1 : 0,
        Trust = s.Scoring.Trust(good, bad), MyVerdict = mine, MyFlags = backstab ? new() { Flags.Backstab } : rook ? new() { Flags.RookOnRook } : new(),
    };

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in FindAll<T>(child)) yield return d;
        }
    }
}
