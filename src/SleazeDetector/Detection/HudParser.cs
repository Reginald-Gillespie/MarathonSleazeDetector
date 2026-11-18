using SleazeDetector.Core;
using SleazeDetector.Ocr;

namespace SleazeDetector.Detection;

[Flags]
public enum SightSource { None = 0, KillFeed = 1, Ping = 2, Prox = 4, Squad = 8, DeathScreen = 16 }

public enum Relation { Unknown, Teammate, Enemy }

/// <param name="ShowBadge">False for names that are tracked but get no meter (teammates in the kill feed / pings).</param>
/// <param name="BadgeX">Where the meter goes if not right after the name (e.g. after a whole kill-feed row), or -1.</param>
public sealed record Sighting(string Name, int X, int Y, int W, int H, SightSource Source, Relation Relation, string? Note = null, double Conf = 1,
                              bool ShowBadge = true, int BadgeX = -1)
{
    public int Right => X + W;
}

public sealed class ZoneResult
{
    public List<Sighting> Sightings { get; } = new();
    public string? SelfName { get; set; }
    /// <summary>Screen box of the name read as your own (from its first word after the slot badge).</summary>
    public (int X, int Y, int W, int H)? SelfBox { get; set; }
    public bool InMatchHud { get; set; }
    public bool DeathScreen { get; set; }
    public bool ExfilScreen { get; set; }
}

/// <summary>A run of words on one row with no icon-sized gap and one colour: one name or phrase.</summary>
public sealed class Segment
{
    public List<OcrWord> Words { get; } = new();
    public string Text => string.Join(' ', Words.Select(w => w.Text));
    public int X => Words[0].X;
    public int Y => Words.Min(w => w.Y);
    public int Right => Words[^1].Right;
    public int H => Words.Max(w => w.H);
    public TextColor Color => Words.GroupBy(w => Bucket(w.Color)).OrderByDescending(g => g.Count()).First().Key;

    public static TextColor Bucket(TextColor c) => c switch
    {
        TextColor.Yellow => TextColor.Green, // Marathon's "friendly" lime sits on the yellow/green border
        TextColor.Blue or TextColor.Other => TextColor.White,
        _ => c,
    };
}

/// <summary>
/// Turns raw OCR lines from a zone into runner sightings and game-state signals.
/// HUD facts (from 1920x1200 captures):
///  - Feed lines (lower-left, above squad list): "KILLER [weapon icon] VICTIM", name colour red = hostile, green = your squad.
///  - Pings: "SQUADMATE PINGED CONTROL" (location, white) or "SQUADMATE PINGED + RUNNER NAME" (pinged runner, red).
///  - Squad list: "[A1] SQUADMATE", indented right of the portraits. Names can contain spaces ("NOVA 42 FOX").
///  - Proximity chat: "NAME#1234" plus a speaker icon, mid-left, red for non-squad runners.
///  - Death recap: "NEURAL LINK SEVERED" / "ELIMINATED ... DMG". Lists who damaged you; used only as the run-end signal.
/// </summary>
public static class HudParser
{
    private static readonly string[] PingWords = { "PINGED", "MARKED", "SPOTTED" };
    private static readonly string[] ActionWords = { "DEPLOYS", "DEPLOYED", "REVIVED", "REVIVING", "USED", "ACTIVATED", "PICKED", "EXFILTRATED" };

    public static ZoneResult Parse(string zone, List<OcrLine> lines, int zoneLeft, int zoneTop, int zoneWidth, int gameLeft, int gameWidth, AppSettings settings,
        int gameTop = 0, int gameHeight = 0)
    {
        var res = new ZoneResult();
        var rows = MergeRows(lines);
        switch (zone)
        {
            case AppSettings.FeedZone: ParseFeed(rows, gameLeft, gameWidth, gameTop, gameHeight, res); break;
            case AppSettings.SelfZone: ParseSelf(rows, res); break;
            case AppSettings.DeathZone: ParseDeath(rows, zoneLeft, zoneWidth, settings, res); break;
            case AppSettings.EndZone: ParseEnd(rows, settings, res); break;
            case AppSettings.ProxZone: ParseProx(rows, res); break;
        }
        return res;
    }

    /// <summary>Group words into visual rows (OCR sometimes splits one HUD line at an icon). Duplicate reads from the two
    /// preprocessing passes are collapsed, keeping the longer reading.</summary>
    public static List<List<OcrWord>> MergeRows(List<OcrLine> lines)
    {
        var rows = new List<List<OcrWord>>();
        foreach (var w in lines.SelectMany(l => l.Words).OrderBy(w => w.CenterY))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].CenterY - w.CenterY) <= Math.Max(6, r[0].H / 2));
            if (row == null) { rows.Add(new List<OcrWord> { w }); continue; }
            var dup = row.FirstOrDefault(o => Overlap(o, w) > 0.5);
            if (dup == null) row.Add(w);
            else if (Score(w) > Score(dup)) { row.Remove(dup); row.Add(w); }
        }
        foreach (var r in rows) r.Sort((a, b) => a.X.CompareTo(b.X));
        return rows;

        static double Overlap(OcrWord a, OcrWord b)
        {
            int ix = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
            return ix <= 0 ? 0 : ix / (double)Math.Min(a.W, b.W);
        }
        // Prefer readings made of clean name characters, then longer ones.
        static int Score(OcrWord w) => w.Text.Count(c => char.IsAsciiLetterOrDigit(c) || c == '_') * 2 - w.Text.Length;
    }

    public static List<Segment> Segments(List<OcrWord> row)
    {
        var segs = new List<Segment>();
        Segment? cur = null;
        foreach (var w in row)
        {
            bool split = cur == null
                || w.X - cur.Right > Math.Max(10, cur.H * 1.1)                              // icon-sized gap
                || (Segment.Bucket(w.Color) != Segment.Bucket(cur.Words[^1].Color)          // colour change (white->red)
                    && w.Text.Length > 1 && cur.Words[^1].Text.Length > 1);
            if (split) { cur = new Segment(); segs.Add(cur); }
            cur!.Words.Add(w);
        }
        return segs;
    }

    private static Relation FromColor(TextColor c) => Segment.Bucket(c) switch
    {
        TextColor.Red => Relation.Enemy,
        TextColor.Green => Relation.Teammate,
        _ => Relation.Unknown,
    };

    private static bool IsWord(OcrWord w, string[] words) =>
        words.Any(k => NameTools.Levenshtein(w.Text.ToUpperInvariant().Trim('.', ',', ':'), k) <= 1);

    private static Sighting? Sight(Segment seg, SightSource src, Relation rel, string? note = null)
    {
        var name = NameTools.CleanName(seg.Words.Select(w => w.Text));
        return name == null ? null : new Sighting(name, seg.X, seg.Y, seg.Right - seg.X, seg.H, src, rel, note, seg.Words.Min(w => w.Conf));
    }

    private static void Add(ZoneResult res, Sighting? s) { if (s != null) res.Sightings.Add(s); }

    /// <summary>True in the app: kill feed and ping names come from FastHud. The OCR-based parsing below is kept for tests.</summary>
    public static bool FeedSquadOnly { get; set; } = true;

    private static void ParseFeed(List<List<OcrWord>> rows, int gameLeft, int gameWidth, int gameTop, int gameHeight, ZoneResult res)
    {
        foreach (var row in rows)
        {
            double relX = (row[0].X - gameLeft) / (double)gameWidth;
            double relY = gameHeight > 0 ? (row[0].CenterY - gameTop) / (double)gameHeight : 0.75;

            // Squad list: indented right of the portraits, in the band just above your own health bar.
            if (relX > 0.07)
            {
                if (relX < 0.2 && relY is > 0.62 and < 0.88)
                {
                    var words = row.Where(w => !NameTools.IsHudWord(w.Text)).ToList();
                    if (words.Count == 0) continue;
                    var seg = new Segment();
                    seg.Words.AddRange(words);
                    var s = Sight(seg, SightSource.Squad, Relation.Teammate);
                    if (s != null) { res.Sightings.Add(s); res.InMatchHud = true; }
                }
                continue; // anything else that far right is world text
            }

            // Kill feed / ping names come from the per-frame colour path (FastHud); here they only signal "in a match".
            if (row.Any(w => IsWord(w, PingWords) || IsWord(w, ActionWords)) || row.Any(w => w.Color is TextColor.Red or TextColor.Green))
                res.InMatchHud = true;
            if (FeedSquadOnly) continue;

            var segs = Segments(row);
            int ping = segs.FindIndex(sg => sg.Words.Any(w => IsWord(w, PingWords)));
            if (ping >= 0)
            {
                var pingSeg = segs[ping];
                int pw = pingSeg.Words.FindIndex(w => IsWord(w, PingWords));
                // Pinger: words before PINGED (same segment or the previous one).
                var pinger = new Segment();
                pinger.Words.AddRange(pw > 0 ? pingSeg.Words.Take(pw) : ping > 0 ? segs[ping - 1].Words : Enumerable.Empty<OcrWord>());
                // The pinger is you or a squadmate: tracked, but no meter.
                if (pinger.Words.Count > 0) Add(res, Sight(pinger, SightSource.Ping, Relation.Teammate) is { } ps ? ps with { ShowBadge = false } : null);
                // Target: a red segment after PINGED is a pinged runner (locations/items are white).
                foreach (var target in segs.Skip(ping + 1).Where(sg => sg.Color == TextColor.Red))
                    Add(res, Sight(target, SightSource.Ping, Relation.Enemy, "Pinged by your squad"));
                res.InMatchHud = true;
                continue;
            }
            if (segs.Any(sg => sg.Words.Any(w => IsWord(w, ActionWords))))
            {
                var first = segs[0];
                var nameWords = first.Words.TakeWhile(w => !IsWord(w, ActionWords)).ToList();
                if (nameWords.Count > 0)
                {
                    var s = new Segment(); s.Words.AddRange(nameWords);
                    var rel = FromColor(s.Color);
                    Add(res, Sight(s, SightSource.KillFeed, rel) is { } sa ? sa with { ShowBadge = rel != Relation.Teammate } : null);
                }
                continue;
            }

            // Kill feed: KILLER [weapon icon] VICTIM
            var named = segs.Where(sg => NameTools.CleanName(sg.Words.Select(w => w.Text)) != null).ToList();
            if (named.Count >= 2)
            {
                var k = named[0]; var v = named[^1];
                var kn = NameTools.CleanName(k.Words.Select(w => w.Text))!;
                var vn = NameTools.CleanName(v.Words.Select(w => w.Text))!;
                // Only enemies get meters in the kill feed. Meters go after the whole row: they never cover the weapon icon
                // or the other name, and the row end is stable even when OCR splits a name into two words. If both
                // names are enemies the overlay puts the second meter beside the first.
                int rowEnd = row.Max(w => w.Right);
                var kr = FromColor(k.Color); var vr = FromColor(v.Color);
                Add(res, Sight(k, SightSource.KillFeed, kr, $"Killed {vn}") is { } ks ? ks with { ShowBadge = kr != Relation.Teammate, BadgeX = rowEnd } : null);
                Add(res, Sight(v, SightSource.KillFeed, vr, $"Killed by {kn}") is { } vs ? vs with { ShowBadge = vr != Relation.Teammate, BadgeX = rowEnd } : null);
            }
            else if (named.Count == 1 && named[0].Color != TextColor.White)
            {
                var rel = FromColor(named[0].Color);
                Add(res, Sight(named[0], SightSource.KillFeed, rel) is { } s1 ? s1 with { ShowBadge = rel != Relation.Teammate } : null);
            }
        }
    }

    private static void ParseSelf(List<List<OcrWord>> rows, ZoneResult res)
    {
        var row = rows.OrderByDescending(r => r.Sum(w => w.W)).FirstOrDefault();
        if (row == null) return;
        var name = NameTools.CleanName(row.Select(w => w.Text));
        if (name == null) return;
        res.SelfName = name; res.InMatchHud = true;
        // The name starts at its first word longer than the slot badge text ("A1").
        var first = row.FirstOrDefault(w => w.Text.Count(char.IsLetterOrDigit) > 2) ?? row[0];
        int right = row.Max(w => w.Right);
        res.SelfBox = (first.X, row.Min(w => w.Y), right - first.X, row.Max(w => w.H));
    }

    private static void ParseDeath(List<List<OcrWord>> rows, int zoneLeft, int zoneWidth, AppSettings s, ZoneResult res)
    {
        string all = string.Join(" ", rows.Select(r => string.Join(" ", r.Select(w => w.Text)))).ToUpperInvariant();
        var header = rows.FirstOrDefault(r => r.Any(w => NameTools.Levenshtein(w.Text.ToUpperInvariant(), "ELIMINATED") <= 2));
        bool severed = s.DeathKeywords.Any(k => k.Contains(' ') && all.Contains(k, StringComparison.OrdinalIgnoreCase))
                       || (all.Contains("NEURAL") && all.Contains("SEVERED"));
        res.DeathScreen = header != null || severed;
        if (!res.DeathScreen) return;

        // The damage rows are everyone who hurt you, not who you met; only the killer is certain. The recap card shows
        // them as "Name#1234" (the one tagged name on the screen), which is how the killer is found.
        foreach (var row in rows)
        {
            // Words keep their spaces ("SILVER CONTRAILS#1234"); OCR sometimes splits the tag off ("NAME #1234").
            var text = System.Text.RegularExpressions.Regex.Replace(string.Join(" ", row.Select(w => w.Text)).Trim(), @"\s+#\s*", "#");
            if (!KillerTag.IsMatch(text)) continue;
            // Runners go by the name the HUD shows (feed, pings): the card's "#1234" is left off.
            var name = NameTools.CleanName(NameTools.Words(text[..text.IndexOf('#')]), hudFilter: false);
            if (name == null) continue;
            var first = row[0];
            res.Sightings.Add(new Sighting(name, first.X, first.Y, row[^1].Right - first.X, first.H, SightSource.DeathScreen,
                                           Relation.Enemy, "Killed you", row.Min(w => w.Conf), ShowBadge: false));
            break;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex KillerTag = new(@"^[A-Za-z0-9_*.\- ]{3,}#[0-9]{4}$");

    private static void ParseEnd(List<List<OcrWord>> rows, AppSettings s, ZoneResult res)
    {
        string all = string.Join(" ", rows.Select(r => string.Join(" ", r.Select(w => w.Text))));
        res.ExfilScreen = s.ExfilKeywords.Any(k => all.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static void ParseProx(List<List<OcrWord>> rows, ZoneResult res)
    {
        foreach (var row in rows)
            foreach (var seg in Segments(row))
            {
                // Prox names are coloured (red = not in your squad). White text here is usually world/UI noise.
                if (seg.Color == TextColor.White) continue;
                var s = Sight(seg, SightSource.Prox, FromColor(seg.Color), "Talked in proximity chat");
                if (s != null && s.Name.Length >= 4) res.Sightings.Add(s);
            }
    }
}
