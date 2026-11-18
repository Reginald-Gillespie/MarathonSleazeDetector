using System.Diagnostics;
using System.Globalization;
using System.Text;
using SleazeDetector.Capture;
using SleazeDetector.Core;
using SleazeDetector.Detection;
using SleazeDetector.Ocr;
using SleazeDetector.Session;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: cuts every kill feed / prox name out of game recordings into a small dataset for training and testing
/// the name reader. Clips can be deleted afterwards; only the name strips are kept.
///   SleazeDetector --harvest outdir clip.mp4|folder ...
/// For each clip a folder outdir/&lt;clip name&gt;/ with one PNG per read attempt (the name plus a margin), reads.tsv
/// (crop file, track, zone, colour, name box inside the crop, game height, video time, current reading, score) and
/// tracks.tsv (track, zone, colour, reads, the run's voted name for it). A clip already harvested is skipped.
/// </summary>
public static class Harvest
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);
        var clips = args.Skip(1).SelectMany(a => Directory.Exists(a)
            ? Directory.GetFiles(a, "*.mp4").OrderBy(f => f).ToArray() : new[] { a }).ToList();
        var font = FontReader.Load();
        var glyphs = GlyphReader.LoadEmbedded();
        double fps = double.TryParse(Environment.GetEnvironmentVariable("HARVEST_FPS"), Inv, out var v) ? v : 10;
        foreach (var clip in clips)
        {
            string dir = Path.Combine(outDir, Safe(Path.GetFileNameWithoutExtension(clip)));
            if (File.Exists(Path.Combine(dir, "tracks.tsv"))) { File.AppendAllText(Path.Combine(outDir, "harvest.log"), $"skip {clip}" + Environment.NewLine); continue; }
            Directory.CreateDirectory(dir);
            var sw = Stopwatch.StartNew();
            int n = One(clip, dir, font, glyphs, fps);
            File.AppendAllText(Path.Combine(outDir, "harvest.log"), $"{Path.GetFileName(clip)}: {n} crops in {sw.Elapsed.TotalSeconds:0}s" + Environment.NewLine);
        }
        return 0;
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    private static (int w, int h) Probe(string clip)
    {
        var psi = new ProcessStartInfo("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{clip}\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var parts = p.StandardOutput.ReadToEnd().Trim().Split(',');
        p.WaitForExit();
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static int One(string clip, string dir, FontReader font, GlyphReader glyphs, double fps)
    {
        var (W, H) = Probe(clip);
        // Only the part of the screen holding the kill feed and prox chat zones is decoded.
        var settings = new AppSettings();
        var zones = settings.Zones.Where(z => z.Name is AppSettings.FeedZone or AppSettings.ProxZone).ToList();
        int cx = 0, cy = (int)(zones.Min(z => z.Y) * H) & ~1;
        int cw = ((int)Math.Ceiling(zones.Max(z => z.X + z.W) * W) + 1) & ~1;
        int ch = (((int)Math.Ceiling(zones.Max(z => z.Y + z.H) * H) - cy) + 1) & ~1;
        var tracker = new RunTracker();
        var start = new DateTime(2000, 1, 1);
        var clock = start;
        var reads = new StringBuilder();
        var tracks = new Dictionary<int, (string zone, TextColor color, int n, Runner? runner)>();
        int count = 0;
        var fast = new FastHud(glyphs, tracker, () => settings)
        {
            Now = () => clock,
            ReadInterval = TimeSpan.FromMilliseconds(300),
            Font = font,
            OnRead = e =>
            {
                var tr = tracks.GetValueOrDefault(e.TrackId, (e.Zone, e.Color, 0, null));
                double capH = 13.0 / 1200 * H;
                int mx = (int)Math.Round(capH * 1.2), my = (int)Math.Round(capH * 0.6);
                // Runs are in screen coordinates; the zone image starts at (ScreenX, ScreenY).
                int bx = e.Box.X0 - e.Frame.ScreenX, by = e.Box.Y0 - e.Frame.ScreenY;
                int x0 = Math.Max(0, bx - mx), y0 = Math.Max(0, by - my);
                int x1 = Math.Min(e.Frame.Width, bx + e.Box.W + mx), y1 = Math.Min(e.Frame.Height, by + e.Box.H + my);
                var crop = e.Frame.Crop(x0, y0, x1 - x0, y1 - y0);
                string file = $"t{e.TrackId:0000}_{tr.Item3:000}.png";
                crop.Save(Path.Combine(dir, file));
                double t = (clock - start).TotalSeconds;
                reads.AppendLine(string.Join('\t', file, e.TrackId, e.Zone, e.Color, bx - x0, by - y0, e.Box.W, e.Box.H, H,
                    t.ToString("0.000", Inv), e.Read?.Raw ?? "", (e.Read?.Score ?? 0).ToString("0.000", Inv)));
                tracks[e.TrackId] = (e.Zone, e.Color, tr.Item3 + 1, e.Runner ?? tr.Item4);
                count++;
            },
        };
        var psi = new ProcessStartInfo("ffmpeg",
            $"-v error -hwaccel auto -i \"{clip}\" -vf \"fps={fps.ToString(Inv)},crop={cw}:{ch}:{cx}:{cy}\" -f rawvideo -pix_fmt bgra pipe:1")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var stream = p.StandardOutput.BaseStream;
        var game = new GameWindow(IntPtr.Zero, 0, 0, W, H, true);
        int frameBytes = cw * ch * 4;
        for (int i = 0; ; i++)
        {
            var buf = new byte[frameBytes];
            int got = 0;
            while (got < frameBytes) { int r = stream.Read(buf, got, frameBytes - got); if (r <= 0) break; got += r; }
            if (got < frameBytes) break;
            var part = new Frame(cw, ch, buf, cx, cy);
            clock = start.AddSeconds(i / fps);
            tracker.HudSeen();   // clips are taken to be from matches
            fast.Tick(game, (x, y, w, h) => part.Crop(x - cx, y - cy, w, h));
        }
        p.WaitForExit();
        File.WriteAllText(Path.Combine(dir, "reads.tsv"), reads.ToString());
        File.WriteAllText(Path.Combine(dir, "tracks.tsv"), string.Concat(tracks.OrderBy(k => k.Key).Select(k =>
            $"{k.Key}\t{k.Value.zone}\t{k.Value.color}\t{k.Value.n}\t{k.Value.runner?.Name ?? ""}\n")));
        return count;
    }
}
