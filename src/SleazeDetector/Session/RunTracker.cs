using SleazeDetector.Detection;

namespace SleazeDetector.Session;

public sealed class Runner
{
    // Base-name reads from the kill feed, pings and squad list (large, crisp text) and from prox chat (small) are kept
    // apart: the former decide the spelling whenever there are any. Only prox chat shows the "#1234" tag; its digits
    // are voted per position across reads.
    private readonly Dictionary<string, int> _spellings = new();
    private readonly Dictionary<string, double> _proxBases = new();
    private readonly Dictionary<string, double> _tags = new();

    public Guid Id { get; } = Guid.NewGuid();
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; set; }
    public int Sightings { get; private set; }
    public SightSource Sources { get; set; }
    public Relation Relation { get; set; }
    public HashSet<string> Notes { get; } = new();
    public bool Confirmed { get; set; }
    /// <summary>User-corrected spelling from the review screen ("NAME#1234"); overrides OCR voting.</summary>
    public string? NameOverride { get; set; }

    public string Name => NameOverride ?? (Tag != null ? $"{BaseName}#{Tag}" : BaseName);
    public string Key => NameTools.RunnerKey(Name);
    /// <summary>True once all four tag digits are known, i.e. the runner can be told apart from others with the same name.</summary>
    public bool HasTag => NameTools.RunnerKey(Name).Contains('#');

    public string BaseName => _spellings.Count > 0 ? Vote(_spellings.ToDictionary(kv => kv.Key, kv => (double)kv.Value)) : _proxBases.Count > 0 ? Vote(_proxBases) : "?";

    /// <summary>Tag digits voted per position over all prox reads; '?' where nothing decoded. Null if never seen in prox chat.</summary>
    public string? Tag
    {
        get
        {
            if (_tags.Count == 0) return null;
            // A clear plurality for one exact reading wins outright; per-digit voting is the fallback, because voting
            // each position separately lets a spread of wrong readings outvote a consistent right one.
            var ranked = _tags.Where(kv => !NameTools.DecodeTag(kv.Key).Contains('?'))
                              .GroupBy(kv => NameTools.DecodeTag(kv.Key))
                              .Select(g => (tag: g.Key, w: g.Sum(kv => kv.Value))).OrderByDescending(t => t.w).ToList();
            double total = _tags.Values.Sum();
            if (ranked.Count > 0 && ranked[0].w >= total * 0.25 && (ranked.Count == 1 || ranked[0].w >= ranked[1].w * 1.5))
                return ranked[0].tag;
            var chars = new char[4];
            for (int i = 0; i < 4; i++)
            {
                var votes = new Dictionary<char, double>();
                foreach (var (tail, n) in _tags)
                    foreach (var (d, w) in NameTools.DigitVotes(tail[i]))
                        votes[d] = votes.GetValueOrDefault(d) + w * n;
                chars[i] = votes.Count == 0 ? '?' : votes.MaxBy(kv => kv.Value).Key;
            }
            return new string(chars);
        }
    }

    public IReadOnlyDictionary<string, double> TagReads => _tags;

    public static (string Base, string? Tag) Parts(Sighting s) =>
        s.Source == SightSource.Prox ? NameTools.SplitTag(s.Name) : (s.Name, null);

    public void Add(Sighting s)
    {
        var (b, tag) = Parts(s);
        // Prox reads vote with their confidence, so frames warped by screen effects barely count.
        if (s.Source == SightSource.Prox) _proxBases[b] = _proxBases.GetValueOrDefault(b) + s.Conf;
        else _spellings[b] = _spellings.GetValueOrDefault(b) + 1;
        if (tag != null) _tags[tag] = _tags.GetValueOrDefault(tag) + s.Conf;
        Sightings++;
    }

    /// <summary>Takes over another runner's reads and history (the two turned out to be the same person).</summary>
    public void MergeFrom(Runner o)
    {
        foreach (var (k, v) in o._spellings) _spellings[k] = _spellings.GetValueOrDefault(k) + v;
        foreach (var (k, v) in o._proxBases) _proxBases[k] = _proxBases.GetValueOrDefault(k) + v;
        foreach (var (k, v) in o._tags) _tags[k] = _tags.GetValueOrDefault(k) + v;
        Sightings += o.Sightings;
        Sources |= o.Sources;
        Notes.UnionWith(o.Notes);
        Confirmed |= o.Confirmed;
        NameOverride ??= o.NameOverride;
        if (o.LastSeen > LastSeen) LastSeen = o.LastSeen;
        if (o.LastProx is { } op && (LastProx is not { } p || op.At > p.At)) LastProx = op;
    }

    public bool Matches(Sighting s)
    {
        var (b, tag) = Parts(s);
        if (NameOverride != null)
        {
            var (ob, ot) = NameTools.SplitTag(NameOverride);
            if (NameTools.Similar(ob, b) && !NameTools.TagsConflict(ot == null ? null : NameTools.DecodeTag(ot),
                                                                    tag == null ? null : NameTools.DecodeTag(tag))) return true;
        }
        if (tag != null && Tag is { } mine)
        {
            var read = NameTools.DecodeTag(tag);
            int diff = Enumerable.Range(0, 4).Count(i => read[i] != '?' && mine[i] != '?' && read[i] != mine[i]);
            // Same tag exactly, base loosely similar: a misread of this runner ("RLNNEP#1234" for "RUNNER#1234").
            if (diff == 0 && !read.Contains('?') && !mine.Contains('?'))
            {
                string kb = NameTools.MatchKey(b), km = NameTools.MatchKey(BaseName);
                if (NameTools.Levenshtein(kb, km) <= Math.Max(2, km.Length / 2)) return true;
            }
            // Two runners sharing a base name in one lobby is rare and tag reads are noisy, so only a firmly
            // established, clearly different tag (3+ digits) splits them (CHRIS#1234 vs CHRIS#5678).
            if (diff >= 3 && _tags.Values.Sum() >= 3) return false;
        }
        return _spellings.Keys.Concat(_proxBases.Keys).Any(k => NameTools.Similar(k, b) || IsFragment(k, b));
    }

    /// <summary>Base-name comparison only (self detection, renames).</summary>
    public bool MatchesBase(string name) =>
        _spellings.Keys.Concat(_proxBases.Keys).Any(k => NameTools.Similar(k, name));

    /// <summary>
    /// OCR sometimes reads only part of a name: "THERUNNER" for "SOMETHERUNNER", "FOX" for "NOVA 42 FOX",
    /// "FACTOR" for "...CONTRACTOR". Treat a read as a fragment when it is a whole word of a multi-word name, or
    /// (5+ chars) appears inside the longer name with at most one wrong character.
    /// </summary>
    private static bool IsFragment(string a, string b)
    {
        var (shortN, longN) = a.Length <= b.Length ? (a, b) : (b, a);
        if (longN.Contains(' ') && longN.Split(' ').Any(t => NameTools.MatchKey(t) == NameTools.MatchKey(shortN))) return true;
        string ks = NameTools.MatchKey(shortN), kl = NameTools.MatchKey(longN);
        if (ks.Length < 5 || ks.Length >= kl.Length) return false;
        for (int i = 0; i + ks.Length <= kl.Length; i++)
        {
            int diff = 0;
            for (int j = 0; j < ks.Length && diff <= 1; j++) if (ks[j] != kl[i + j]) diff++;
            if (diff <= 1) return true;
        }
        return false;
    }

    /// <summary>
    /// The name is the reading seen most often: exact readings are counted, the most common wins (ties: the longer
    /// one). A read of only part of the name (a line still sliding in or fading, the first word covered: "GRENADIER"
    /// for "UESC GRENADIER") is evidence for the full name it was cut from, so it counts for every longer reading it is
    /// a fragment of rather than competing with it. No letter-by-letter blending, which can produce a spelling that was
    /// never actually on screen.
    /// </summary>
    private static string Vote(Dictionary<string, double> spellings)
    {
        double Support(string k) => spellings.Sum(o => o.Key == k ? o.Value
            : NameTools.MatchKey(o.Key).Length < NameTools.MatchKey(k).Length && IsFragment(o.Key, k) ? o.Value : 0);
        return spellings.Keys.Select(k => (k, s: Support(k), own: spellings[k]))
                        .OrderByDescending(x => x.s).ThenByDescending(x => x.own).ThenByDescending(x => x.k.Length).First().k;
    }

    /// <summary>Diagnostics: every reading of this runner's name and how often it was seen, most common first.</summary>
    public string SpellingSummary =>
        string.Join(", ", _spellings.Select(kv => (k: kv.Key, v: (double)kv.Value)).Concat(_proxBases.Select(kv => (k: kv.Key, v: kv.Value)))
                                    .OrderByDescending(x => x.v).Select(x => $"{x.k} x{x.v:0.#}"));

    // Where this runner's name was last seen in the prox list; reads at the same spot moments later are the same speaker.
    public (int X, int Y, int H, int Len, DateTime At)? LastProx { get; set; }

    /// <summary>Same row of the prox list moments ago, with a loosely similar base name: the same speaker.</summary>
    public bool IsSameProxSpot(Sighting s)
    {
        if (LastProx is not { } p || DateTime.Now - p.At >= TimeSpan.FromSeconds(6)) return false;
        if (Math.Abs(p.Y - s.Y) > Math.Max(4, p.H * 0.6) || Math.Abs(p.X - s.X) > Math.Max(8, p.H * 2) || Math.Abs(p.Len - s.Name.Length) > 3) return false;
        string kb = NameTools.MatchKey(Parts(s).Base), mine = NameTools.MatchKey(BaseName);
        return NameTools.Levenshtein(kb, mine) <= Math.Max(2, mine.Length / 2);
    }
}

/// <summary>One match ("run"): the set of runners seen, their roles and what happened.</summary>
public sealed class RunSession
{
    public Guid RunId { get; } = Guid.NewGuid();
    public DateTime Started { get; } = DateTime.Now;
    public DateTime? Ended { get; set; }
    public string EndReason { get; set; } = "";
    public List<Runner> Runners { get; } = new();
    public IEnumerable<Runner> Confirmed => Runners.Where(r => r.Confirmed);
}

/// <summary>Owns the current run, merges noisy sightings into runners and decides when a run starts/ends.</summary>
public sealed class RunTracker
{
    private readonly object _gate = new();
    private DateTime _lastHud = DateTime.MinValue;
    // After a run ends no new run starts until the end condition is gone: death / exfil screen not seen for a few
    // seconds, or (manual end, game closed) the match HUD gone.
    private bool _awaitingLobby;
    private string _endReason = "";
    private DateTime _endScreenSeen = DateTime.MinValue;
    private DateTime _lastTick = DateTime.MinValue;
    private static readonly TimeSpan EndScreenGone = TimeSpan.FromSeconds(5);

    public RunSession? Current { get; private set; }
    private string? _selfName;

    /// <summary>Your own name: never tracked or rated. Setting it drops any runner already created from it.</summary>
    public string? SelfName
    {
        get => _selfName;
        set
        {
            if (value == _selfName) return;
            lock (_gate)
            {
                Core.Log.Info($"Own name: {value}");
                _selfName = value;
                // Your own name is never drawn in enemy red, so a runner seen in red is never you.
                if (value != null) Current?.Runners.RemoveAll(r => r.Relation != Relation.Enemy && r.MatchesBase(value));
            }
        }
    }

    public event Action<Runner>? RunnerConfirmed;
    public event Action<RunSession>? RunEnded;
    public event Action<RunSession>? RunStarted;

    /// <summary>Longer than any match. A run still open after this was left without an end screen (quit to the
    /// menu mid-match); the next match's HUD closes it.</summary>
    private static readonly TimeSpan LongestMatch = TimeSpan.FromMinutes(50);
    public const string MatchOver = "match left earlier";

    public void HudSeen()
    {
        bool stale;
        lock (_gate)
        {
            _lastHud = DateTime.Now;
            stale = Current != null && DateTime.Now - Current.Started > LongestMatch;
        }
        if (stale) EndSignal(MatchOver);
        lock (_gate) if (Current == null && !_awaitingLobby) StartRun();
    }

    /// <summary>
    /// Weaker evidence of a match (red or green feed text, your name without its badge checked): keeps a running run
    /// from timing out, but never starts one. Menus show the same kinds of text.
    /// </summary>
    public void HudActivity()
    {
        lock (_gate) if (Current != null) _lastHud = DateTime.Now;
    }

    /// <summary>The death or exfil screen is showing right now (keeps the end latched until it is gone).</summary>
    public void EndScreenSeen()
    {
        lock (_gate) _endScreenSeen = DateTime.Now;
    }

    /// <summary>Also catches merged reads like "WOLF OLF" (your name plus a fragment of it).</summary>
    private bool IsSelf(string name) =>
        _selfName != null && (NameTools.Similar(_selfName, name) || name.Split(' ').Any(t => t.Length >= 3 && NameTools.Similar(_selfName, t)));

    private void StartRun()
    {
        Current = new RunSession();
        _lastHud = DateTime.Now;
        Core.Log.Info($"Run started {Current.RunId}");
        RunStarted?.Invoke(Current);
    }

    /// <returns>The runner the sighting was attributed to, or null if it was ignored (self / junk).</returns>
    public Runner? Add(Sighting s, int minSightings)
    {
        lock (_gate)
        {
            if (IsSelf(Runner.Parts(s).Base)) return null;
            if (Current == null)
            {
                // The recap's killer can be read just after the run ended (an end banner came first): it belongs to
                // the run that just ended.
                if (s.Source == SightSource.DeathScreen && _lastEnded is { Ended: { } at } last && DateTime.Now - at < TimeSpan.FromSeconds(30))
                    return AddLate(last, s);
                // A run starts only from the ability bar (HudSeen). Names read outside a run are menu text or leftovers.
                return null;
            }
            var run = Current!;
            if (s.Source is SightSource.KillFeed or SightSource.Prox or SightSource.Ping) _lastHud = DateTime.Now;
            // Prox: the same spot in the list moments ago is the strongest evidence (tag reads are noisy), so it goes first.
            var r = (s.Source == SightSource.Prox ? run.Runners.FirstOrDefault(x => x.IsSameProxSpot(s)) : null)
                    ?? run.Runners.FirstOrDefault(x => x.Matches(s));
            if (r == null)
            {
                r = new Runner { FirstSeen = DateTime.Now };
                run.Runners.Add(r);
            }
            r.Add(s);
            r.LastSeen = DateTime.Now;
            if (s.Source == SightSource.Prox) r.LastProx = (s.X, s.Y, s.H, s.Name.Length, DateTime.Now);
            r.Sources |= s.Source;
            if (s.Note != null) r.Notes.Add(s.Note);
            // Kill-feed colour / squad list are authoritative; never downgrade a known relation to Unknown.
            if (s.Relation != Relation.Unknown && (r.Relation == Relation.Unknown || s.Source is SightSource.Squad or SightSource.KillFeed))
                r.Relation = s.Relation;

            if (!r.Confirmed && r.Sightings >= minSightings)
            {
                r.Confirmed = true;
                Core.Log.Info($"Runner confirmed: {r.Name} ({r.Relation}, {r.Sources})");
                RunnerConfirmed?.Invoke(r);
            }
            return r;
        }
    }

    /// <summary>
    /// Adds a read of a name that is already being tracked on screen (same line, earlier frames) to that runner, without
    /// re-matching by spelling: continuity on screen beats a noisy read.
    /// </summary>
    public Runner? AddTo(Runner r, Sighting s, int minSightings)
    {
        lock (_gate)
        {
            if (Current == null || !Current.Runners.Contains(r)) return Add(s, minSightings);
            _lastHud = DateTime.Now;
            r.Add(s);
            r.LastSeen = DateTime.Now;
            // A line's first reads can be wrong (sliding in, flashing, partly covered) and start a runner of their
            // own; its later, better reads then match the runner it really is. Same person: merge into the one
            // with more evidence.
            var other = Current.Runners.FirstOrDefault(o => o != r && o.Relation == r.Relation && o.Matches(s));
            if (other != null)
            {
                var (keep, drop) = other.Sightings >= r.Sightings ? (other, r) : (r, other);
                keep.MergeFrom(drop);
                Current.Runners.Remove(drop);
                Core.Log.Info($"Runners merged: {drop.Name} -> {keep.Name}");
                r = keep;
            }
            r.Sources |= s.Source;
            if (s.Note != null) r.Notes.Add(s.Note);
            if (s.Source == SightSource.Prox) r.LastProx = (s.X, s.Y, s.H, s.Name.Length, DateTime.Now);
            if (!r.Confirmed && r.Sightings >= minSightings)
            {
                r.Confirmed = true;
                Core.Log.Info($"Runner confirmed: {r.Name} ({r.Relation}, {r.Sources})");
                RunnerConfirmed?.Invoke(r);
            }
            return r;
        }
    }

    private RunSession? _lastEnded;

    /// <summary>A runner added to a run after it ended (the killer from the death recap).</summary>
    public event Action<RunSession>? LateRunner;

    private Runner AddLate(RunSession run, Sighting s)
    {
        var r = run.Runners.FirstOrDefault(x => x.Matches(s));
        bool isNew = r == null || !r.Confirmed;
        if (r == null) { r = new Runner { FirstSeen = DateTime.Now }; run.Runners.Add(r); }
        r.Add(s);
        r.LastSeen = DateTime.Now;
        r.Sources |= s.Source;
        if (s.Note != null && !r.Notes.Contains(s.Note)) r.Notes.Add(s.Note);
        if (r.Relation == Relation.Unknown) r.Relation = s.Relation;
        r.Confirmed = true;
        if (isNew)
        {
            Core.Log.Info($"Runner confirmed after the run ended: {r.Name} ({r.Relation}, {r.Sources})");
            LateRunner?.Invoke(run);
        }
        return r;
    }

    /// <summary>Drops the current run without a review (what it was watching went away, e.g. the clip player was
    /// swapped for the game): those runners were never met in this game.</summary>
    public void Abandon(string reason)
    {
        lock (_gate)
        {
            if (Current == null) return;
            Core.Log.Info($"Run abandoned ({reason}); {Current.Confirmed.Count()} runners");
            Current = null;
            _awaitingLobby = false;
        }
    }

    public void EndSignal(string reason)
    {
        RunSession? ended;
        lock (_gate)
        {
            if (Current == null) return;
            _awaitingLobby = reason != MatchOver;
            _endReason = reason;
            ended = Current;
            ended.Ended = DateTime.Now;
            ended.EndReason = reason;
            Current = null;
            _lastEnded = ended;
        }
        Core.Log.Info($"Run ended ({reason}); {ended.Confirmed.Count()} runners");
        // Names read several ways: keep the readings in the log to see how bad the misreads get.
        foreach (var r in ended.Confirmed.Where(r => r.SpellingSummary.Contains(", ")))
            Core.Log.Info($"  Readings of {r.Name}: {r.SpellingSummary}");
        RunEnded?.Invoke(ended);
    }

    /// <summary>
    /// Called periodically. Never ends a run (a run ends only on the death or exfil screen, the game closing, or
    /// ending it by hand); it only lets the next run start once the previous end is over.
    /// </summary>
    /// <param name="gameVisible">False while the game (or clip) is hidden behind another window: that time does not
    /// count as the end screen or the HUD being gone.</param>
    public void Tick(bool gameVisible = true)
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            if (!gameVisible && _lastTick != DateTime.MinValue)
            {
                var paused = now - _lastTick;
                if (_lastHud != DateTime.MinValue) _lastHud += paused;
                if (_endScreenSeen != DateTime.MinValue) _endScreenSeen += paused;
            }
            _lastTick = now;
            if (!_awaitingLobby) return;
            bool clear = _endReason is "eliminated" or "exfiltrated"
                ? now - _endScreenSeen > EndScreenGone
                : now - _lastHud > TimeSpan.FromSeconds(15);
            if (clear) _awaitingLobby = false;
        }
    }
}
