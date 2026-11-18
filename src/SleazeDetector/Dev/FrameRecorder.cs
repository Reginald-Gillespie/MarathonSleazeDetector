using System.Collections.Concurrent;
using SleazeDetector.Capture;
using SleazeDetector.Core;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: records exactly what the per-frame HUD path reads live, so a live problem can be replayed offline with
/// --fast-test frame for frame. Start it with the F10 hotkey (or by creating the file "record.flag" in the data folder).
/// A recording starts with the last <see cref="LookbackSeconds"/> seconds before the key was pressed (kept in memory
/// all along, at up to <see cref="LookbackFps"/> frames a second), then records every tick for <see cref="AfterSeconds"/>
/// seconds while the game (or clip) is visible (extended by every further press), and deletes the flag. Each tick becomes one PNG the size of the game area with
/// the zones that were grabbed pasted in (everything else black), plus a line in times.txt with the tick's time in
/// milliseconds, so the replay uses the live timing.
/// Output: DataDir\debug\rec-yyyyMMdd-HHmmss\
/// </summary>
public static class FrameRecorder
{
    // Seconds recorded after the key; pressing it again while recording extends the same recording.
    private const double AfterSeconds = 15;
    private const double LookbackSeconds = 6, LookbackFps = 15;
    public const string Hotkey = "F10";

    private sealed record Tick(DateTime Time, GameWindow Game, List<Frame> Pieces);

    private static readonly string FlagFile = Path.Combine(Paths.DataDir, "record.flag");
    // Pieces are composed and saved on a background thread; the game loop only hands over the grabbed frames.
    private static readonly BlockingCollection<(Tick tick, string path)> Queue = new(boundedCapacity: 400);
    private static readonly Queue<Tick> Recent = new();
    private static DateTime _nextFlagCheck = DateTime.MinValue;
    private static bool _flag;
    private static string? _dir;
    private static int _count;
    private static DateTime _start;
    private static Tick? _tick;
    private static DateTime _endAt;
    private static DateTime _lastPress = DateTime.MinValue;

    static FrameRecorder()
    {
        new Thread(() =>
        {
            foreach (var (tick, path) in Queue.GetConsumingEnumerable())
                try { Compose(tick).Save(path); } catch (Exception ex) { Log.Error("Frame recorder save failed", ex); }
        }) { IsBackground = true, Name = "frame-recorder" }.Start();
    }

    /// <summary>Starts a recording (same as creating the flag file); beeps so it is heard over the game.</summary>
    public static void Start()
    {
        try
        {
            File.WriteAllText(FlagFile, "");
            _nextFlagCheck = DateTime.MinValue;
            _lastPress = DateTime.Now;
            System.Media.SystemSounds.Asterisk.Play();
            Log.Info("Frame recorder: started by hotkey");
        }
        catch (Exception ex) { Log.Error("Frame recorder: could not start", ex); }
    }

    /// <summary>Wraps a grab function for one tick: what it grabs is kept for the look-back and for recordings.</summary>
    public static Func<int, int, int, int, Frame> Wrap(GameWindow game, Func<int, int, int, int, Frame> grab)
    {
        if (DateTime.Now >= _nextFlagCheck) { _flag = File.Exists(FlagFile); _nextFlagCheck = DateTime.Now.AddSeconds(1); }
        var tick = new Tick(DateTime.Now, game, new List<Frame>());
        _tick = tick;
        return (x, y, w, h) =>
        {
            var f = grab(x, y, w, h);
            f.Keep();
            tick.Pieces.Add(f);
            return f;
        };
    }

    /// <summary>Call after the tick: keeps it for the look-back, or queues it when recording.</summary>
    public static void EndTick()
    {
        var tick = _tick;
        _tick = null;
        if (tick == null) return;
        if (!_flag)
        {
            _dir = null;
            // Look-back: frames at most LookbackFps apart, LookbackSeconds long (a few hundred MB at 2880 x 1800).
            if (Recent.Count == 0 || (tick.Time - Recent.Last().Time).TotalSeconds >= 1 / LookbackFps) Recent.Enqueue(tick);
            while (Recent.Count > 0 && (tick.Time - Recent.Peek().Time).TotalSeconds > LookbackSeconds) Recent.Dequeue();
            return;
        }
        if (_dir == null)
        {
            _dir = Path.Combine(Paths.DebugDir, $"rec-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(_dir);
            _count = 0;
            while (Recent.Count > 0 && (tick.Time - Recent.Peek().Time).TotalSeconds > LookbackSeconds) Recent.Dequeue();
            _start = Recent.Count > 0 ? Recent.Peek().Time : tick.Time;
            int before = Recent.Count;
            // (Frames left over from before the game was hidden were dropped just above.)
            while (Recent.Count > 0) Save(Recent.Dequeue());
            _endAt = tick.Time.AddSeconds(AfterSeconds);
            Log.Info($"Frame recorder: recording to {_dir} ({before} frames from before the key)");
        }
        // Pressed again while recording: keep going until 15 s after the latest press.
        if (_lastPress.AddSeconds(AfterSeconds) > _endAt) _endAt = _lastPress.AddSeconds(AfterSeconds);
        Save(tick);
        if (tick.Time >= _endAt)
        {
            try { File.Delete(FlagFile); } catch { }
            Log.Info($"Frame recorder: done, {_count} frames in {_dir}");
            _flag = false; _dir = null;
        }
    }

    private static void Save(Tick tick)
    {
        var name = $"f{_count:D4}.png";
        if (!Queue.TryAdd((tick, Path.Combine(_dir!, name)))) return;   // dropped if the disk can't keep up
        File.AppendAllText(Path.Combine(_dir!, "times.txt"), $"{name} {(tick.Time - _start).TotalMilliseconds:0}{Environment.NewLine}");
        _count++;
    }

    /// <summary>The game area with the grabbed zones pasted in where they were on screen.</summary>
    private static Frame Compose(Tick tick)
    {
        var game = tick.Game;
        var canvas = new Frame(game.Width, game.Height, new byte[game.Width * game.Height * 4], game.X, game.Y);
        foreach (var f in tick.Pieces)
            for (int row = 0; row < f.Height; row++)
            {
                int cy = f.ScreenY - game.Y + row;
                if (cy < 0 || cy >= canvas.Height) continue;
                int cx0 = Math.Max(0, f.ScreenX - game.X), fx0 = cx0 - (f.ScreenX - game.X);
                int n = Math.Min(f.Width - fx0, canvas.Width - cx0);
                if (n > 0) Buffer.BlockCopy(f.Bgra, (row * f.Width + fx0) * 4, canvas.Bgra, (cy * canvas.Width + cx0) * 4, n * 4);
            }
        return canvas;
    }
}
