using System.Text.RegularExpressions;
using SleazeDetector.Capture;
using SleazeDetector.Core;
using SleazeDetector.Ocr;
using SleazeDetector.Session;

namespace SleazeDetector.Detection;

/// <summary>
/// The per-frame path for the kill feed and prox chat: finds coloured names from pixels (HudRuns), keeps track of each
/// one while it is on screen, reads its text with the glyph matcher (once when it appears, then cheap re-reads to
/// refine the spelling / tag), feeds the run tracker, and returns where every meter should be right now. A meter
/// appears the frame a name is read, follows the line as it moves, and is dropped the moment the name is gone.
/// </summary>
public sealed class FastHud
{
    private sealed class Tracked
    {
        public required string Zone;
        public int Id;
        public TextRun Run = null!;
        public TextColor Color;
        public GlyphReader.Result? Read;
        public string? Name;                 // cleaned name (null = not a runner / unreadable)
        public Runner? Runner;
        public DateTime NextRead;
        public DateTime LastSeen;
        public BadgeRequest? LastBadge;
        public bool NotesDone;
        // Where this name's meter is drawn. Kept still unless the line really moved: a line never shifts sideways, so
        // a shorter line end for a few frames is a missed word (glitch effects), not a move.
        public bool HasPos;
        public int BX, BCenterY, MaxRuns;
        public DateTime? ShrinkSince;
        // Per character cell, the glyphs read there so far weighted by match score: one garbled frame (glow, an
        // effect) can't change the name, and a letter misread on some frames is outvoted by the others.
        public Dictionary<char, double>[]? Votes;
        // Since when the name has been hidden under an icon (see Tick); null while visible.
        public DateTime? CoveredSince;
        public DateTime NextWiden;
        // Reads in a row that are a different name from the one voted so far (see ReadAndReport).
        public int Different;
        // The line has been seen as a ping ("PINGED" before the name). Kept for as long as the line is tracked.
        public bool Ping;
    }

    // Height of HUD name ink as a fraction of the game height (~13 px at 1200p, ~19 px at 1800p).
    private const double CapFrac = 13.0 / 1200;
    // Character pitch of the HUD font as a fraction of the game height (10 px at 1200p).
    private const double PitchFrac = 10.0 / 1200;
    private int _gameHeight = 1200;
    // A name that drops out for a moment (screen effects) keeps its meter this long.
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(150);
    // A known feed name hidden under a name-coloured icon stays this long at most.
    private static readonly TimeSpan MaxHidden = TimeSpan.FromSeconds(3);
    private static readonly Regex TagPattern = new(@"^\S.{2,}#[0-9]{4}$");

    private readonly GlyphReader _glyphs;
    private readonly RunTracker _tracker;
    private readonly Func<AppSettings> _settings;
    private readonly List<Tracked> _tracked = new();
    // Where feed lines start (screen x, in buckets of half a cap height -> times seen). Every feed line begins at the
    // same left edge; it is learned from lines with two names on them (killer and victim, pinger and target).
    private readonly Dictionary<int, int> _lineStarts = new();
    private int _lineStartsHeight;

    public FastHud(GlyphReader glyphs, RunTracker tracker, Func<AppSettings> settings)
    {
        _glyphs = glyphs; _tracker = tracker; _settings = settings;
    }

    public bool IsReady => _glyphs.IsReady || Font != null;

    /// <summary>
    /// Reader using the game's own HUD font (loaded from the install in the background); when set it reads every name,
    /// otherwise the learned glyph templates do.
    /// </summary>
    public FontReader? Font { get; set; }

    // The font reader knows the font's pitch to within a few percent: 10.3 px per 1200 px of game height in the kill
    // feed, 9.3 in prox chat (a smaller size of the same font).
    private const double FeedPitchFrac = 10.3 / 1200, ProxPitchFrac = 9.3 / 1200;

    /// <summary>Learned reader (embedded model); when set it reads every name, the font matcher only what it cannot.</summary>
    public NameNet? Net { get; set; }

    private GlyphReader.Result? ReadFresh(Frame frame, int x, int y, int w, int h, bool prox)
    {
        // With the learned reader, an unsure read means "not a name": the font matcher would happily read scenery.
        if (Net != null) return NetRead(frame, x, y, w, h, prox);
        return Font is { } fr ? fr.ReadRun(frame, x, y, w, h, (prox ? ProxPitchFrac : FeedPitchFrac) * _gameHeight, tagged: prox)
                              : _glyphs.ReadRun(frame, x, y, w, h, PitchFrac * _gameHeight);
    }

    /// <summary>
    /// The learned reader's text as a cell-by-cell result. The font is monospaced, so the grid follows from the box:
    /// n cells span the ink minus about a quarter cell of side bearings, at the HUD's pitch give or take a few percent.
    /// Null when the model is unsure: an average score under 0.85 or any character under 0.4 (scenery, screen
    /// effects, half-drawn lines).
    /// </summary>
    private GlyphReader.Result? NetRead(Frame frame, int x, int y, int w, int h, bool prox)
    {
        var r = Net!.Read(frame, x, y, w, h, _gameHeight);
        if (r == null || r.Text.Length == 0 || r.Score < 0.85 || r.CharConf.Min() < 0.4) return null;
        // A tag is '#' and exactly four digits; anything after it is the speaker icon or scenery.
        int hash = r.Text.IndexOf('#');
        if (hash >= 1 && hash + 5 <= r.Text.Length && r.Text.Skip(hash + 1).Take(4).All(char.IsAsciiDigit) && r.Text.Length > hash + 5)
            r = new NameNet.Reading(r.Text[..(hash + 5)], r.CharConf[..(hash + 5)], r.CharConf[..(hash + 5)].Average());
        double est = (prox ? ProxPitchFrac : FeedPitchFrac) * _gameHeight;
        int n = r.Text.Length;
        double pitch = Math.Clamp(w / Math.Max(0.75, n - 0.25), est * 0.95, est * 1.05);
        // A prox box can run on past the tag (the speaker icon): there the pitch is the HUD's.
        if (prox && w / Math.Max(0.75, n - 0.25) > est * 1.05) pitch = est;
        return new GlyphReader.Result(r.Text, r.Score, r.CharConf, pitch, x - pitch * 0.12, y, h, Raw: r.Text, Learned: true);
    }

    private GlyphReader.Result? ReadAgain(Frame frame, int x, int y, int w, int h, GlyphReader.Result prev, bool prox) =>
        Net != null ? ReadFresh(frame, x, y, w, h, prox)
        : Font is { } fr && prev.Sigma > 0 ? fr.ReRead(frame, x, y, w, h, prev)
        : Font != null ? ReadFresh(frame, x, y, w, h, prox)
        : _glyphs.ReRead(frame, x, y, w, h, prev);

    /// <summary>Clock (tests replay frames at video time instead of wall time).</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>Test override: re-read interval for every name (null = 250 ms prox / 500 ms feed).</summary>
    public TimeSpan? ReadInterval { get; init; }

    /// <summary>Test hook: one line per read attempt (text, scores, outcome).</summary>
    public Action<string>? Trace { get; init; }

    /// <summary>Dev hook (dataset harvesting): every read attempt, with the zone image, the box read and its reading.</summary>
    public sealed record ReadEvent(int TrackId, string Zone, TextColor Color, Frame Frame, TextRun Box, GlyphReader.Result? Read, Runner? Runner);
    public Action<ReadEvent>? OnRead { get; init; }
    private int _nextId;
    // Reads per frame are capped: a screen effect can light up a dozen red blobs at once, and reading them all in
    // one tick would freeze the meters. The rest are read on the next frames.
    private const int MaxReadsPerTick = 4;
    private int _readsThisTick;

    public sealed record VisibleName(TextRun Run, TextColor Color, string? Name, bool Confirmed);

    /// <summary>Names on screen after the last tick (diagnostics / tests).</summary>
    public IReadOnlyList<VisibleName> Visible { get; private set; } = Array.Empty<VisibleName>();

    /// <summary>Diagnostics for the last tick: per zone whether it was read and how many lines / names it had.</summary>
    public string LastDiag { get; private set; } = "";

    /// <summary>One frame: returns the complete set of meters for the kill feed and prox zones.</summary>
    public List<BadgeRequest> Tick(GameWindow game, Func<int, int, int, int, Frame> grab, Func<int, int, int, int, bool>? zoneVisible = null)
    {
        if (_clear) { _tracked.Clear(); _clear = false; }
        _readsThisTick = 0;
        var s = _settings();
        double capH = CapFrac * game.Height;
        _gameHeight = game.Height;
        var badges = new List<BadgeRequest>();
        var seen = new HashSet<Tracked>();
        var diag = new List<string>();
        foreach (var z in s.Zones.Where(z => z.Name is AppSettings.FeedZone or AppSettings.ProxZone))
        {
            int x = game.X + (int)(z.X * game.Width), y = game.Y + (int)(z.Y * game.Height);
            int w = (int)(z.W * game.Width), h = (int)(z.H * game.Height);
            if (zoneVisible != null && !zoneVisible(x, y, w, h)) { diag.Add($"{z.Name}:covered"); continue; }
            var frame = grab(x, y, w, h);
            try
            {
            var found = HudRuns.Find(frame, capH, out var occluders);
            diag.Add($"{z.Name}:lines={found.Count},runs={found.Sum(r => r.Runs.Count)}");
            bool feed = z.Name == AppSettings.FeedZone;
            var zoneSeen = new List<Tracked>();
            var maps = new List<Dictionary<TextRun, Tracked>>();

            // 1. Which tracked name each run is. A feed line never changes in place, so what is seen of a known name
            //    on the same line (part of it, or several pieces when an icon cuts through it) is that name.
            foreach (var row in found)
            {
                if (feed) LearnLineStart(row, capH);
                // By reference: a tracked name keeps its own box, which may differ from the run seen this frame.
                var map = new Dictionary<TextRun, Tracked>(ReferenceEqualityComparer.Instance);
                maps.Add(map);
                foreach (var run in row.Runs)
                {
                    var piece = feed ? zoneSeen.FirstOrDefault(t => t.Color == run.Color && Inside(run, t.Run, capH)) : null;
                    if (piece != null) { map[run] = piece; continue; }
                    var m = Match(z.Name, run, capH, seen);
                    var t = m ?? New(z.Name, run);
                    if (m == null || !feed) t.Run = run;
                    else
                    {
                        (t.Run, bool moved) = Trim(t, run, capH);
                        // Read while still moving (sliding in, still fading): start the reading over where it settles.
                        if (moved && t.Read != null) { t.Read = null; t.Votes = null; t.NextRead = Now(); }
                    }
                    // Part of a known name covered: keep the name and its meter, read it again once it is whole.
                    // Likewise while a name-coloured icon touches it: what shows may be only part of the name.
                    bool covered = feed && m != null && run.W < t.Run.W - capH * 0.6
                                   || occluders.Any(o => o.X0 < run.X1 + capH && o.X1 > run.X0 - capH && o.Y0 < run.Y1 && o.Y1 > run.Y0);
                    t.LastSeen = Now(); t.CoveredSince = null;
                    map[run] = t;
                    seen.Add(t); zoneSeen.Add(t);
                    // Squad (lime) names are never read here: the squad list provides them, and they get no meter.
                    // A name still fading in (or cut by an effect) is shorter than full height: wait a frame for a clean read.
                    // More of the name showing than was read (it was first read while still being drawn in): offer
                    // the wider box; the longer reading is kept only if the extra part reads as characters.
                    int? longer = feed && m != null && !covered && t.Read != null && Now() >= t.NextWiden
                                  && run.X1 > TextEnd(t) + t.Read.Pitch * 0.8 ? run.X1 : null;
                    if (longer != null) t.NextWiden = Now() + TimeSpan.FromMilliseconds(250);
                    if (run.Color != TextColor.Green && !covered && (Now() >= t.NextRead || longer != null) && run.H >= capH * 0.8
                        && _readsThisTick < MaxReadsPerTick)
                    {
                        _readsThisTick++;
                        ReadAndReport(t, frame, row, z, capH, map, longer);
                        OnRead?.Invoke(new ReadEvent(t.Id, z.Name, t.Color, frame, t.Run, t.Read, t.Runner));
                    }
                }
            }

            // 2. Known feed names missing this frame but lying under a name-coloured blob (an icon or marker passing
            //    over them, merged with their ink) are covered, not gone: they keep their place for a while.
            var hidden = new List<Tracked>();
            if (feed)
                foreach (var t in _tracked.Where(t => t.Zone == z.Name && !seen.Contains(t) && (t.Read != null || t.Color == TextColor.Green)))
                {
                    if (!occluders.Any(o => o.X0 < t.Run.X1 && o.X1 > t.Run.X0 && o.Y0 < t.Run.Y1 && o.Y1 > t.Run.Y0)) continue;
                    t.CoveredSince ??= Now();
                    if (Now() - t.CoveredSince > MaxHidden) continue;
                    t.LastSeen = Now();
                    hidden.Add(t); seen.Add(t);
                    if (t.LastBadge != null) badges.Add(t.LastBadge);
                }

            // 3. Meters: every enemy on a line, at the end of the line.
            for (int i = 0; i < found.Count; i++)
            {
                var row = found[i];
                foreach (var t in row.Runs.Select(r => maps[i][r]).Distinct())
                {
                    if (t.Runner is { Confirmed: true } r && r.Relation != Relation.Teammate && t.Name != null)
                    {
                        Place(t, row, feed ? FeedRight(t, row, capH, maps[i], hidden) : ProxRight(t), capH, neverShrink: !feed);
                        int hh = (int)Math.Round(capH);
                        t.LastBadge = new BadgeRequest(r.Id, r.Key, z.Name, t.BX, t.BCenterY - hh / 2, hh);
                        badges.Add(t.LastBadge);
                    }
                    else t.LastBadge = null;
                }
            }
            }
            finally { frame.Release(); }
        }
        // Names not seen this frame keep their meter for a brief grace period (glitch effects), then they are gone.
        foreach (var t in _tracked.Where(t => !seen.Contains(t) && t.LastBadge != null && Now() - t.LastSeen <= Grace))
            badges.Add(t.LastBadge!);
        _tracked.RemoveAll(t => Now() - t.LastSeen > Grace);
        LastDiag = string.Join(" ", diag) + $" meters={badges.Count}";
        Visible = _tracked.Where(t => seen.Contains(t)).Select(t => new VisibleName(t.Run, t.Color, t.Name, t.Runner?.Confirmed == true)).ToList();
        return badges;
    }

    /// <summary>
    /// End of a read name from the text itself (the font is monospaced: start plus characters times pitch), so a
    /// coloured blob touching the name (a red ping marker, scenery) never moves the meter. Box end if never read.
    /// </summary>
    private static int TextEnd(Tracked t)
    {
        if (t.Read is not { } rd) return t.Run.X1;
        int len = rd.Raw.Length > 0 ? rd.Raw.TrimEnd().Length : rd.Text.Length;
        return Math.Min(t.Run.X1, t.Run.X0 + (int)Math.Round(len * rd.Pitch - rd.Pitch * 0.15));
    }

    /// <summary>
    /// A tracked name's box this frame. Kill feed and ping names are left aligned and never grow leftwards, and once
    /// read their length is known: ink joining on either side (a ping marker over the line, a red light behind it)
    /// is cut off, so the name keeps its box, its reading grid and its meter.
    /// </summary>
    private static (TextRun Box, bool Moved) Trim(Tracked t, TextRun run, double capH)
    {
        int tol = (int)Math.Ceiling(capH * 0.6), step = (int)Math.Max(2, Math.Round(capH * 0.15));
        int dx0 = run.X0 - t.Run.X0, dx1 = run.X1 - t.Run.X1;
        // Both ends moved the same way: the line itself moved (it slides in from the left when it appears). Follow it.
        if ((dx0 >= step && dx1 >= step) || (dx0 <= -step && dx1 <= -step)) return (run, true);
        // Otherwise only one end changed. Started further left: ink joined on the left. Started further right: the
        // first letters are covered. Ended early: the end is covered. Either way the name itself is where it was.
        bool known = t.Read != null || t.Color == TextColor.Green;
        int x0 = Math.Abs(dx0) > tol && known ? t.Run.X0 : run.X0;
        int x1 = known && run.X1 < t.Run.X1 - tol ? t.Run.X1 : run.X1;
        if (t.Read is { } rd)
        {
            int len = rd.Raw.Length > 0 ? rd.Raw.TrimEnd().Length : rd.Text.Length;
            int end = x0 + (int)Math.Ceiling(len * rd.Pitch + rd.Pitch * 0.3);
            if (x1 > end) x1 = end;
        }
        return (x0 == run.X0 && x1 == run.X1 ? run : run with { X0 = x0, X1 = Math.Max(x1, x0 + 1) }, false);
    }

    /// <summary>
    /// End of a kill feed / ping line for the meter: the last name on it that was actually read, chained from this name
    /// across a weapon icon or "PINGED" (up to ~10 caps). A red / lime blob of scenery further along the same height
    /// never moves the meter out to it.
    /// </summary>
    private static int FeedRight(Tracked t, TextRow row, double capH, Dictionary<TextRun, Tracked> map, List<Tracked> hidden)
    {
        int right = TextEnd(t), cy = (row.Y0 + row.Y1) / 2;
        // The names on this line: seen ones, plus known ones hidden under an icon right now.
        var line = row.Runs.Select(r => map[r])
                      .Concat(hidden.Where(h => Math.Abs(h.Run.CenterY - cy) <= capH * 0.5))
                      .Distinct().Where(o => o != t && o.Run.X0 >= t.Run.X1 - 1).OrderBy(o => o.Run.X0);
        foreach (var o in line)
        {
            if (o.Run.X0 - right > capH * 10) break;
            // Squad (lime) names are never read here, but their colour alone says they are names.
            if (o.Color == TextColor.Green) right = Math.Max(right, o.Run.X1);
            else if (o.Read is { } rd && LooksLikeName(rd.Text.Trim())) right = Math.Max(right, TextEnd(o));
        }
        return right;
    }

    /// <summary>A run lying within a known name's box on the same line (the rest of the name covered).</summary>
    private static bool Inside(TextRun run, TextRun box, double capH)
    {
        double tol = capH * 0.6;
        return Math.Abs(run.CenterY - box.CenterY) <= capH * 0.5 && run.X0 >= box.X0 - tol && run.X1 <= box.X1 + tol;
    }

    /// <summary>
    /// Prox lines sit over the 3D scene, so "follow the ink" would wander into red scenery and faint tag digits come
    /// and go. Each prox line holds one name; its end is the read's start plus characters times pitch. The speaker
    /// icon right after the tag is included when it is seen, but nothing further than an icon's width.
    /// </summary>
    private static int ProxRight(Tracked t)
    {
        if (t.Read is not { } rd) return t.Run.X1;
        int textEnd = t.Run.X0 + (int)Math.Round(rd.Text.Length * rd.Pitch - rd.Pitch * 0.15);
        return Math.Clamp(t.Run.X1, textEnd, textEnd + (int)Math.Round(rd.Pitch * 2.2));
    }

    /// <summary>
    /// Meter position with memory: it moves up/down only when the line moved by more than a few pixels (a new line
    /// pushed it), moves right at once if more of the line shows up, and moves left only once the shorter end has
    /// held for a while (a different line took the spot). One-frame misses of the last word never move it.
    /// </summary>
    private void Place(Tracked t, TextRow row, int right, double capH, bool neverShrink = false)
    {
        int tol = Math.Max(3, (int)Math.Round(capH * 0.3));
        // Centre on the letters themselves: capital height down from the top of this name's ink. The line's band can
        // reach lower (underscores, icons), which would put the meter below the text.
        int cy = (int)Math.Round(t.Run.Y0 + Math.Min(t.Run.H, capH) / 2.0);
        var now = Now();
        if (!t.HasPos)
        {
            t.HasPos = true; t.BX = right; t.BCenterY = cy; t.MaxRuns = row.Runs.Count; t.ShrinkSince = null;
            return;
        }
        // Follow the text up and down exactly (feed lines glide up as new ones arrive); a dead band of one pixel only,
        // so the meter never trails below a line that has moved up a little.
        if (Math.Abs(cy - t.BCenterY) > 1) t.BCenterY = cy;
        if (right > t.BX + tol || (right > t.BX && row.Runs.Count >= t.MaxRuns)) { t.BX = right; t.ShrinkSince = null; }
        else if (right < t.BX - tol && !neverShrink)
        {
            t.ShrinkSince ??= now;
            // Fewer names on the line than before = the victim's name was missed; wait longer before believing it.
            var wait = TimeSpan.FromMilliseconds(row.Runs.Count < t.MaxRuns ? 900 : 250);
            if (now - t.ShrinkSince >= wait) { t.BX = right; t.MaxRuns = row.Runs.Count; t.ShrinkSince = null; }
        }
        else t.ShrinkSince = null;
        t.MaxRuns = Math.Max(t.MaxRuns, row.Runs.Count);
    }

    /// <summary>
    /// The tracked name this run is: same colour, near the same height, and the same start, or, when ink joined on
    /// the left (a ping marker), the same end with the start further left. Extra ink on the right is allowed once read.
    /// </summary>
    private Tracked? Match(string zone, TextRun run, double capH, HashSet<Tracked> taken) =>
        _tracked.Where(t => !taken.Contains(t) && t.Zone == zone && t.Color == run.Color
                            && Math.Abs(t.Run.CenterY - run.CenterY) <= capH * 4
                            && ((Math.Abs(t.Run.X0 - run.X0) <= capH * 0.6 && run.W >= t.Run.W - capH * 0.9
                                 && (run.W <= t.Run.W + capH * 0.9 || (t.Read != null && zone == AppSettings.FeedZone)))
                                || (zone == AppSettings.FeedZone && Math.Abs(t.Run.X1 - run.X1) <= capH * 0.6 && t.Read != null)
                                || (zone == AppSettings.FeedZone && (t.Read != null || t.Color == TextColor.Green) && Inside(run, t.Run, capH))))
                .OrderBy(t => Math.Abs(t.Run.CenterY - run.CenterY)).FirstOrDefault();

    private Tracked New(string zone, TextRun run)
    {
        var t = new Tracked { Zone = zone, Run = run, Color = run.Color, Id = ++_nextId };
        _tracked.Add(t);
        return t;
    }

    private void ReadAndReport(Tracked t, Frame frame, TextRow row, Zone z, double capH, Dictionary<TextRun, Tracked> map, int? extendTo = null)
    {
        var run = t.Run;
        // Prox names are fully re-read each time (the tag needs independent reads to vote on); feed names reuse the grid.
        bool proxZone = z.Name == AppSettings.ProxZone;
        GlyphReader.Result? r = null;
        if (extendTo is int ex && t.Read is { } prev)
        {
            var wide = ReadFresh(frame, run.X0, run.Y0, ex - run.X0, run.H, proxZone);
            if (wide != null) wide = SpacesFromColour(wide, frame, t.Color);
            int n = prev.CellScores.Length;
            if (wide != null && wide.CellScores.Length > n && wide.Score >= 0.68
                && Enumerable.Range(n, wide.CellScores.Length - n).All(i => wide.Raw[i] == ' ' || wide.CellScores[i] >= 0.6))
            {
                r = wide; t.Read = null; t.Votes = null;
                t.Run = run = run with { X1 = ex };
            }
            else if (Now() < t.NextRead) return;   // offered early only for the wider read
        }
        r ??= t.Read == null || proxZone
            ? ReadFresh(frame, run.X0, run.Y0, run.W, run.H, proxZone)
            : ReadAgain(frame, run.X0, run.Y0, run.W, run.H, t.Read, proxZone);
        if (r != null) r = SpacesFromColour(r, frame, t.Color);
        bool prox = z.Name == AppSettings.ProxZone;
        // Re-read prox names often (tag digits are voted across reads); feed names a few times while visible.
        t.NextRead = Now() + (ReadInterval ?? TimeSpan.FromMilliseconds(prox ? 250 : 500));
        Trace?.Invoke($"  read {run.X0},{run.Y0} {run.W}x{run.H}: '{r?.Raw}' score={r?.Score:0.00} weak={r?.CellScores.Count(c => c < 0.45)}/{r?.CellScores.Length} cells=[{(r == null ? "" : string.Join(" ", r.CellScores.Select(c => c.ToString("0.00"))))}]");
        if (r == null || r.Score < 0.68 || r.CellScores.Count(c => c < 0.45) > r.CellScores.Length / 4) return;
        // A feed line replaced in place by a new one (the old one expired, the next slid into its spot) keeps the
        // same box, so it is the same tracked line here. Two clean reads in a row of a clearly different name (not
        // similar, not part of it) mean a new name: start over, so it is never added to the previous runner.
        if (!prox && t.Votes != null && t.Read != null)
        {
            var voted = new string(t.Votes.Select(v => v.MaxBy(kv => kv.Value).Key).ToArray()).Trim();
            var now = r.Text.Trim();
            bool different = r.Score >= 0.8 && now.Length >= 3 && voted.Length >= 3 && !NameTools.Similar(now, voted)
                             && !NameTools.MatchKey(voted).Contains(NameTools.MatchKey(now)) && !NameTools.MatchKey(now).Contains(NameTools.MatchKey(voted));
            t.Different = different ? t.Different + 1 : 0;
            if (t.Different >= 2)
            {
                Trace?.Invoke($"  -> '{now}' replaces '{voted}' on this line: new name");
                t.Read = null; t.Votes = null; t.Runner = null; t.Name = null; t.NotesDone = false; t.Different = 0; t.Ping = false;
            }
            else if (different) return;   // wait for a second read before believing it
        }
        if (t.Read == null || r.Score >= t.Read.Score - 0.05) t.Read = r;

        var text = r.Text.Trim();
        if (!prox && r.Raw.Length > 0)
        {
            // Feed re-reads use the same grid, so cell i is the same character every time: vote per cell.
            if (t.Votes == null || t.Votes.Length != r.Raw.Length)
                t.Votes = Enumerable.Range(0, r.Raw.Length).Select(_ => new Dictionary<char, double>()).ToArray();
            for (int i = 0; i < r.Raw.Length; i++)
                t.Votes[i][r.Raw[i]] = t.Votes[i].GetValueOrDefault(r.Raw[i]) + r.CellScores[i];
            text = new string(t.Votes.Select(v => v.MaxBy(kv => kv.Value).Key).ToArray()).Trim();
        }
        if (prox ? !TagPattern.IsMatch(text) : text.Contains('#')) return;   // tags only exist in prox chat
        if (!LooksLikeName(text)) return;
        // Red text alone on its line that does not start at the feed's left edge is a game message in the name colour
        // ("OUT OF STOCK", "VAULT FULL"), not a feed line. Names already known are kept (their partner may be covered).
        if (!prox && t.Runner == null && LoneOnLine(t, row, capH) && !AtLineStart(t.Run, capH) && !TextLeadsTo(t.Run, frame, capH))
        {
            Trace?.Invoke($"  -> '{text}' rejected: alone on its line, not at the feed's left edge");
            return;
        }
        // A ping shows a red '+' before the pinged name ("+ RUNNER"), and two names can sit in one colour box with
        // the '+' between them ("RUNNER+OTHER"): the '+' is never part of a name; the first name is kept.
        text = text.TrimStart('+', ' ');
        if (text.IndexOf('+') is int plus and > 0) text = text[..plus].TrimEnd();
        var name = NameTools.CleanName(NameTools.Words(text), hudFilter: false, exact: r.Learned);
        // A ping line: "PINGED" (white text) right before this name, or a long white gap between the line's names.
        // A kill line has a weapon icon there.
        // A line seen as a ping once stays one (a frame can be blurred or partly covered).
        int whiteText = prox ? 0 : HudRuns.WhiteTextLeft(frame, t.Run.Y0, t.Run.Y1, t.Run.X0, capH);
        if (!prox && ((row.Runs.Count >= 2 && row.WhiteBetween > capH * 4) || whiteText >= capH * 1.8)) t.Ping = true;
        bool pingRow = t.Ping;
        if (name == null || NameTools.IsIgnored(name, _settings().IgnoredNames, partial: pingRow)) { t.Name = null; t.Runner = null; return; }
        t.Name = name;

        var rel = t.Color == TextColor.Green ? Relation.Teammate : Relation.Enemy;
        // A feed line with "PINGED +" (a long white gap) between two names is a ping, not a kill.
        var src = prox ? SightSource.Prox : pingRow ? SightSource.Ping : SightSource.KillFeed;
        double conf = Math.Clamp((r.Score - 0.68) / 0.24, 0.05, 1.0);
        var sighting = new Sighting(name, run.X0, run.Y0, run.W, run.H, src, rel, prox ? "Talked in proximity chat" : null, conf * conf);
        // A new name becomes a runner only once it has read the same on two frames: a glitch effect can break a line
        // into pieces that each read cleanly for a frame ("JOHN", "KCO"), but never the same way twice. The second
        // read is taken on the very next frame, so a real name gets its meter one frame later. A read that does not
        // agree with the pending one starts over (it never confirms someone else's runner). Names already known in
        // this run match their runner by spelling and show at once.
        if (t.Runner is { Confirmed: false } pending && !SameRead(NameTools.SplitTag(name).Base, pending.BaseName, r.Learned))
            t.Runner = null;
        var runner = t.Runner != null ? _tracker.AddTo(t.Runner, sighting, 2) : _tracker.Add(sighting, 2);
        Trace?.Invoke($"  -> '{name}' {src} (white text left {whiteText}px) conf={conf:0.00} runner={(runner == null ? "null" : runner.Name + (runner.Confirmed ? "" : "(unconfirmed)"))}");
        if (runner == null) return;
        t.Runner = runner;
        if (!runner.Confirmed) t.NextRead = Now();

        if (!t.NotesDone && !prox && row.Runs.Count >= 2)
        {
            // Two names on one feed line: killer then victim, unless a long white "PINGED +" sits between them.
            bool ping = row.WhiteBetween > capH * 4;
            // The line's own run for this name (t.Run may be a trimmed copy of it).
            var self = map.FirstOrDefault(kv => kv.Value == t).Key;
            var other = row.Runs.FirstOrDefault(o => !ReferenceEquals(o, self));
            var otherT = other == null ? null : map.GetValueOrDefault(other);
            if (!ping && otherT?.Name != null)
            {
                runner.Notes.Add(ReferenceEquals(row.Runs[0], self) ? $"Killed {otherT.Name}" : $"Killed by {otherT.Name}");
                t.NotesDone = true;
            }
            else if (ping && ReferenceEquals(self, row.Runs[^1])) { runner.Notes.Add("Pinged by your squad"); t.NotesDone = true; }
        }
    }

    /// <summary>Scene noise read as text tends to be one glyph repeated ("PPPPFPPPP"); names are varied.</summary>
    /// <summary>
    /// The name's colour says where its characters are: a cell with (almost) no red / lime ink is a space, whatever
    /// the shape matcher made of the scenery showing through the gap between two words. Without this a background
    /// line in the gap reads as "I" and "UESC ASSAULT" becomes "UESCIASSAULT", a different name.
    /// </summary>
    private static GlyphReader.Result SpacesFromColour(GlyphReader.Result r, Frame frame, TextColor color)
    {
        var raw = (r.Raw.Length > 0 ? r.Raw : r.Text).ToCharArray();
        var scores = r.CellScores.ToArray();
        if (scores.Length != raw.Length) return r;
        bool changed = false;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == ' ') continue;
            int cx = (int)Math.Round(r.X0 + (i + 0.1) * r.Pitch), cw = Math.Max(1, (int)Math.Round(r.Pitch * 0.8));
            // Down to below the baseline: an underscore is a bar under the line.
            int cy = (int)Math.Round(r.Y0 - r.H * 0.05), ch = Math.Max(1, (int)Math.Round(r.H * 1.4));
            // A real glyph's strokes cover a good part of its cell; 3% is a speck of scenery.
            if (HudRuns.ColorInk(frame, cx, cy, cw, ch, color) < cw * ch * 0.03) { raw[i] = ' '; scores[i] = 0.9; changed = true; }
        }
        if (!changed) return r;
        var s = new string(raw);
        return r with { Raw = s, Text = s.Trim(), CellScores = scores, Score = scores.Average() };
    }

    /// <summary>
    /// Two reads of a new name agree. The learned reader reads a real name the same way every time, so its reads must
    /// match exactly (one character off in a long name); noise does not. The font matcher
    /// confuses look-alike letters, so its reads are compared loosely.
    /// </summary>
    private static bool SameRead(string a, string b, bool learned)
    {
        if (!learned) return NameTools.Similar(a, b);
        a = a.ToUpperInvariant(); b = b.ToUpperInvariant();
        return a == b || (Math.Min(a.Length, b.Length) >= 8 && NameTools.Levenshtein(a, b) <= 1);
    }

    private static bool LooksLikeName(string text)
    {
        var letters = text.Where(char.IsAsciiLetterOrDigit).ToList();
        if (letters.Count < 3) return false;
        int maxRun = 1, run = 1;
        for (int i = 1; i < letters.Count; i++) { run = letters[i] == letters[i - 1] ? run + 1 : 1; maxRun = Math.Max(maxRun, run); }
        if (maxRun >= 4) return false;
        var top = letters.GroupBy(c => c).Max(g => g.Count());
        return letters.Count < 6 || top <= letters.Count / 2;
    }

    private void LearnLineStart(TextRow row, double capH)
    {
        if (_lineStartsHeight != _gameHeight) { _lineStarts.Clear(); _lineStartsHeight = _gameHeight; }
        // Two names with a gap between them (an icon or "PINGED +"): a real feed line.
        if (row.Runs.Count < 2 || row.Runs[1].X0 - row.Runs[0].X1 < capH) return;
        int b = (int)(row.Runs[0].X0 / (capH * 0.5));
        _lineStarts[b] = _lineStarts.GetValueOrDefault(b) + 1;
        // Keep it adaptive (the game window can move): halve all counts now and then.
        if (_lineStarts[b] > 600) foreach (var k in _lineStarts.Keys.ToList()) _lineStarts[k] /= 2;
    }

    /// <summary>The feed's left edge (screen x), once seen on enough two-name lines.</summary>
    private double? LineStart(double capH)
    {
        if (_lineStarts.Count == 0) return null;
        var top = _lineStarts.MaxBy(kv => kv.Value);
        if (top.Value < 3) return null;
        // A start on a bucket border splits the peak: use the leftmost bucket of it.
        int b = top.Key;
        while (_lineStarts.GetValueOrDefault(b - 1) >= Math.Max(1, top.Value / 3)) b--;
        return b * capH * 0.5;
    }

    /// <summary>A line starting at the feed's left edge, or left of it (still sliding in).</summary>
    private bool AtLineStart(TextRun run, double capH) => LineStart(capH) is double x && run.X0 <= x + capH * 1.5;

    /// <summary>
    /// HUD text runs from the feed's left edge up to this name without a break wider than an icon: the rest of a feed
    /// line ("SQUADMATE PINGED" before the target) is there, even if the squad name in it wasn't picked out this frame.
    /// </summary>
    private bool TextLeadsTo(TextRun run, Frame frame, double capH)
    {
        int gap = (int)(capH * 4);
        if (LineStart(capH) is double start)
            return HudRuns.TextReachLeft(frame, run.Y0, run.Y1, run.X0, (int)(start - capH * 2), gap) <= start + capH * 1.5;
        // Left edge not learned yet: a word's worth of HUD text right before the name ("PINGED +", a weapon icon).
        return HudRuns.TextReachLeft(frame, run.Y0, run.Y1, run.X0, (int)(run.X0 - capH * 12), gap) <= run.X0 - capH * 2.5;
    }

    /// <summary>No other name on the line: every run of it lies within this name's box.</summary>
    private static bool LoneOnLine(Tracked t, TextRow row, double capH)
        => row.Runs.All(r => r.X0 >= t.Run.X0 - capH * 0.6 && r.X1 <= t.Run.X1 + capH * 0.6);

    private volatile bool _clear;

    /// <summary>Forget everything on screen (run ended / game hidden). Safe to call from any thread.</summary>
    public void Clear() => _clear = true;
}
