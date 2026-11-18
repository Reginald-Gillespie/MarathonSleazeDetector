using SleazeDetector.Capture;
using SleazeDetector.Core;
using SleazeDetector.Ocr;
using SleazeDetector.Session;

namespace SleazeDetector.Detection;

/// <param name="Slot">Which HUD element the name is in (prox list, kill feed, squad list...). One badge per runner per slot.</param>
/// <param name="X">Screen x of the name's right edge.</param>
/// <param name="Y">Screen y of the name's top.</param>
/// <param name="H">Height of the name's text.</param>
public sealed record BadgeRequest(Guid RunnerId, string Key, string Slot, int X, int Y, int H);

/// <summary>
/// The capture -> OCR -> parse loop. Each zone is OCR'd on its own interval; only small screen regions are
/// grabbed, so the cost is a few ms per tick. Nothing touches the game process; it only reads pixels.
/// </summary>
public sealed class Scanner
{
    private readonly HudOcr _ocr;
    private readonly RunTracker _tracker;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<string, DateTime> _due = new();
    private readonly Dictionary<string, int> _selfVotes = new();
    private int _deathStreak;

    public event Action<List<BadgeRequest>>? Badges;

    public Scanner(HudOcr ocr, RunTracker tracker, Func<AppSettings> settings)
    {
        _ocr = ocr; _tracker = tracker; _settings = settings;
    }

    private int _barStreak;

    /// <summary>Process every zone that is due. <paramref name="grab"/> returns pixels for a screen rect.</summary>
    /// <param name="zoneVisible">Optional check that a zone's screen area really shows the game (not a window in front of it).</param>
    public async Task TickAsync(GameWindow game, Func<int, int, int, int, Frame> grab, bool force = false,
                                Func<int, int, int, int, bool>? zoneVisible = null)
    {
        var s = _settings();
        var badges = new List<BadgeRequest>();
        // The ability bar is the sign of a match: seen on two scans in a row, a run starts (or keeps going).
        var barBox = AbilityBar.Box(game);
        if (zoneVisible == null || zoneVisible(barBox.X, barBox.Y, barBox.W, barBox.H))
        {
            _barStreak = AbilityBar.Score(game, grab) >= AbilityBar.Threshold ? _barStreak + 1 : 0;
            if (_barStreak >= 2) _tracker.HudSeen();
        }
        // Kill feed names and prox chat are handled every frame by FastHud; this slower OCR path reads the squad
        // list (teammates), your own name, and the death / exfil screens.
        foreach (var z in s.Zones.Where(z => z.Enabled && z.Name != AppSettings.ProxZone))
        {
            if (!force && _due.TryGetValue(z.Name, out var due) && DateTime.Now < due) continue;
            _due[z.Name] = DateTime.Now.AddMilliseconds(z.IntervalMs);

            int x = game.X + (int)(z.X * game.Width), y = game.Y + (int)(z.Y * game.Height);
            int w = (int)(z.W * game.Width), h = (int)(z.H * game.Height);
            if (zoneVisible != null && !zoneVisible(x, y, w, h)) continue;
            var frame = grab(x, y, w, h);

            // Several preprocessing passes catch text on different backgrounds; rows are de-duplicated in the parser.
            var lines = new List<OcrLine>();
            foreach (var pass in z.Passes)
                if (Enum.TryParse<Preprocess>(pass, true, out var mode))
                    lines.AddRange(await _ocr.ReadAsync(frame, z.Scale, mode));

            var res = HudParser.Parse(z.Name, lines, x, y, w, game.X, game.Width, s, game.Y, game.Height);
            // Squad names are white; yellow text in the same spot is an ability / action ("SEARCH AND DESTROY").
            res.Sightings.RemoveAll(sg => sg.Source == SightSource.Squad && IsYellowText(frame, sg));
            // A squad name sits right after its slot badge ("[A1] NAME") and is white. Kill feed / ping lines reach into
            // the same screen area; a red or lime name there, or one without a badge, is not a squadmate.
            double capH = 13.0 / 1200 * game.Height;
            res.Sightings.RemoveAll(sg => sg.Source == SightSource.Squad
                                          && (!HudRuns.HasSlotBadge(frame, sg.X, sg.Y, sg.H, capH)
                                              || HudRuns.ColorInk(frame, sg.X, sg.Y, sg.W, sg.H, TextColor.Red) + HudRuns.ColorInk(frame, sg.X, sg.Y, sg.W, sg.H, TextColor.Green) > sg.W * sg.H / 20));
            // In a match your own name sits right after your squad slot badge ("[A1] NAME"). Text in the same spot of a
            // menu (the vault's "Back", "Hide Menu") mostly lacks one; your name is also only taken during a run.
            if (z.Name == AppSettings.SelfZone && res.SelfName != null
                && (_tracker.Current == null || res.SelfBox is not { } sb || !HudRuns.HasSlotBadge(frame, sb.X, sb.Y, sb.H, capH)))
                res.SelfName = null;
            Apply(z, res, badges);
        }
        _tracker.Tick();
        if (badges.Count > 0) Badges?.Invoke(badges);
    }

    private static bool IsYellowText(Frame f, Sighting sg)
    {
        var (name, yellow) = HudRuns.NameVsYellow(f, sg.X, sg.Y, sg.W, sg.H);
        return yellow >= Math.Max(8, sg.H) && yellow > name;
    }

    private void Apply(Zone z, ZoneResult res, List<BadgeRequest> badges)
    {
        // Text the parser took for match HUD keeps a running run going; only the ability bar starts one (TickAsync).
        if (res.InMatchHud) _tracker.HudActivity();

        if (res.SelfName != null)
        {
            _selfVotes[res.SelfName] = _selfVotes.GetValueOrDefault(res.SelfName) + 1;
            // A reading becomes your name once it has been read the same way at least 3 times and at least twice as
            // often as any other reading.
            var top = _selfVotes.OrderByDescending(kv => kv.Value).First();
            int second = _selfVotes.Where(kv => kv.Key != top.Key).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            if (top.Value >= 3 && top.Value >= second * 2) _tracker.SelfName = top.Key;
        }

        // Both OCR passes usually read the same word; count it once per scan.
        var unique = new List<Sighting>();
        foreach (var sg in res.Sightings)
            if (!unique.Any(u => Math.Abs(u.Y - sg.Y) < 8 && Math.Abs(u.X - sg.X) < 30 && NameTools.Similar(u.Name, sg.Name)))
                unique.Add(sg);

        foreach (var sg in unique)
        {
            var runner = _tracker.Add(sg, z.MinSightings);
        }

        if (z.Name == AppSettings.DeathZone)
        {
            _deathStreak = res.DeathScreen ? _deathStreak + 1 : 0;
            if (res.DeathScreen) _tracker.EndScreenSeen();
            // Two consecutive reads (~3s) so the killer list has been captured before closing the run.
            if (_deathStreak == 2) _tracker.EndSignal("eliminated");
        }
        if (res.ExfilScreen) { _tracker.EndScreenSeen(); _tracker.EndSignal("exfiltrated"); }
    }
}
