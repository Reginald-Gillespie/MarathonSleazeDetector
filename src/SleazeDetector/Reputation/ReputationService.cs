using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SleazeDetector.Core;

namespace SleazeDetector.Reputation;

/// <summary>
/// Rating needs a Steam sign-in. Your ratings are saved locally first (my-reports.json), then sent to the shared
/// server; any that could not be sent (offline) go up on the next try. Lookups merge community totals (server, no
/// sign-in needed) with your own verdicts (local).
/// </summary>
public sealed class ReputationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Func<AppSettings> _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly ConcurrentDictionary<string, (RepSummary rep, DateTime at)> _cache = new();
    private readonly object _fileGate = new();
    private List<Report> _reports;

    public event Action<string>? Updated; // match key

    public ReputationService(Func<AppSettings> settings)
    {
        _settings = settings;
        _http.DefaultRequestHeaders.Add("User-Agent", $"MarathonSleazeDetector/{AppVersion.Display}");
        _reports = LoadReports();
        if (Untag(_reports)) SaveReports();
        ClaimUnowned();
        Account.Changed += OnAccountChanged;
        if (Account.SignedIn) _ = LoadMyWeightAsync();
    }

    /// <summary>The shared ratings server (server/ in the repository), fixed in the build.</summary>
    public const string ServerUrl = "https://sleaze-detector.sleaze-detector-server.workers.dev";

    public static string? Server => (ServerOverride ?? ServerUrl) is { Length: > 0 } s ? s.TrimEnd('/') : null;

    /// <summary>Dev tools only: talk to another server (a local test copy).</summary>
    public static string? ServerOverride { get; set; }

    /// <summary>How long to wait before sending changes again after the server could not take them (offline, its
    /// rate limits, trouble on its side).</summary>
    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Your Steam sign-in for sharing ratings.</summary>
    public SteamAccount Account { get; } = new();

    /// <summary>A request with your sign-in; a 401 answer means the sign-in is gone (expired, or banned).</summary>
    private async Task<HttpResponseMessage?> Authed(HttpMethod method, string url, object? body = null)
    {
        var token = Account.Token;
        if (Server == null || token == null) return null;
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = JsonContent.Create(body, options: Json);
        var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) Account.SignOut();
        return resp;
    }

    /// <summary>Ratings of the account signed in now (none while signed out).</summary>
    public IReadOnlyList<Report> MyReports { get { lock (_fileGate) return Mine().ToList(); } }

    /// <summary>
    /// Ratings are always someone's: the ones saved here are only shown and sent while that same Steam account is
    /// signed in. Signed out, the app has no ratings of its own (rating needs a sign-in), and a different account
    /// signing in on this PC never sees or sends them. Call under _fileGate.
    /// </summary>
    private IEnumerable<Report> Mine()
    {
        var id = Account.SteamId;
        return id == null || Account.Token == null ? Enumerable.Empty<Report>() : _reports.Where(r => r.SteamId == id);
    }

    /// <summary>Ratings saved before they recorded their account belong to the account signed in at the time.</summary>
    private void ClaimUnowned()
    {
        lock (_fileGate)
        {
            if (Account.SteamId is not { } id || !_reports.Any(r => r.SteamId == null)) return;
            foreach (var r in _reports.Where(r => r.SteamId == null)) r.SteamId = id;
            SaveReports();
        }
    }

    private void OnAccountChanged()
    {
        ClaimUnowned();
        // What the server counted for "your" rating belonged to the previous account; take-backs that were not sent
        // yet can no longer be (they needed that account's sign-in).
        _mineOnServer.Clear();
        lock (_fileGate) _pendingDeletes.Clear();
        foreach (var k in _cache.Keys.ToArray()) Publish(k);
        if (Account.SignedIn)
        {
            _ = LoadMyWeightAsync();
            _ = SyncAsync();
            _ = RefreshAsync(_cache.Keys.ToArray());
        }
    }

    private volatile bool _playingRook;

    /// <summary>
    /// You are playing Rook this run: Rook on Rook reports count. Changing it re-scores every cached runner and
    /// announces each one, so meters on screen update at once.
    /// </summary>
    public bool PlayingRook
    {
        get => _playingRook;
        set
        {
            if (_playingRook == value) return;
            _playingRook = value;
            Log.Info($"Playing Rook: {value}");
            foreach (var k in _cache.Keys.ToArray()) Publish(k);
        }
    }

    private double? TrustOf(RepSummary s) => _settings().Scoring.Trust(s.WeightedGood, s.WeightedBad, s.WeightedRookBad, s.WeightedRookGood, _playingRook);

    // A runner's meter is everyone else's ratings plus your own, kept apart so a change of your rating shows at once
    // and never counts twice. _others: the server's totals minus your rating. _mineOnServer: what the server counts for
    // your rating (it knows your account's weight), as of its last answer about this runner.
    private readonly ConcurrentDictionary<string, RepSummary> _others = new();
    private readonly ConcurrentDictionary<string, RepSummary> _mineOnServer = new();
    // Changes are sent one at a time, in the order they were made. A lookup answered while a change was on its way (or
    // sent before one) may predate the change and is dropped; the change's own answer carries the new totals.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _seqGate = new();
    private readonly Dictionary<string, int> _writeSeq = new(), _writing = new();
    // Ratings taken back that the server has not been told about yet (offline, or signed out).
    private readonly HashSet<string> _pendingDeletes = new();
    // How many people one of your ratings counts as on the server (an admin or trusted account counts as several).
    private double _myWeight = 1;

    /// <summary>Cached summary for the overlay (never blocks). Kicks off a refresh if missing/stale.</summary>
    public RepSummary? Peek(string key)
    {
        if (_cache.TryGetValue(key, out var hit))
        {
            if (DateTime.UtcNow - hit.at > TimeSpan.FromMinutes(10)) _ = RefreshAsync(new[] { key });
            return hit.rep;
        }
        _cache[key] = (Display(key), DateTime.UtcNow.AddMinutes(-9)); // placeholder, refreshed soon
        _ = RefreshAsync(new[] { key });
        return _cache[key].rep;
    }

    public async Task RefreshAsync(IEnumerable<string> keys)
    {
        var list = keys.Distinct().ToList();
        if (list.Count == 0 || Server == null) return;
        Dictionary<string, int> seq;
        lock (_seqGate) seq = list.ToDictionary(k => k, k => _writeSeq.GetValueOrDefault(k));
        Dictionary<string, RepSummary> remote;
        try
        {
            var url = $"{Server}/v1/reputation?keys={Uri.EscapeDataString(string.Join(",", list))}";
            var got = await _http.GetFromJsonAsync<List<RepSummary>>(url, Json);
            remote = (got ?? new()).ToDictionary(r => r.Key);
        }
        catch (Exception ex) { Log.Error("Reputation lookup failed", ex); return; }
        foreach (var k in list)
        {
            lock (_seqGate)
                if (_writing.GetValueOrDefault(k) > 0 || _writeSeq.GetValueOrDefault(k) != seq[k]) continue;
            if (!remote.TryGetValue(k, out var total))
            {
                // Nobody's rating counts on the server, yours included.
                _others[k] = new RepSummary { Key = k };
                _mineOnServer[k] = new RepSummary { Key = k };
            }
            else
            {
                // No answer from the server about your rating since the app started: estimate its part.
                Report? mine;
                lock (_fileGate) mine = Resolve(k);
                var mineThere = _mineOnServer.TryGetValue(k, out var m) ? m
                    : mine is { Synced: true } ? Estimate(mine) : new RepSummary { Key = k };
                _others[k] = Add(total, mineThere, -1);
            }
            Publish(k);
        }
    }

    /// <summary>Recomputes a runner's meter from what is known now and announces it.</summary>
    private void Publish(string key)
    {
        _cache[key] = (Display(key), DateTime.UtcNow);
        Updated?.Invoke(key);
    }

    private RepSummary Display(string key)
    {
        Report? mine;
        lock (_fileGate) mine = Resolve(key);
        var others = _others.TryGetValue(key, out var o) ? o : new RepSummary { Key = key };
        RepSummary my = mine == null ? new RepSummary { Key = key }
            : mine.Synced && _mineOnServer.TryGetValue(key, out var m) ? m
            : Estimate(mine);
        var s = Add(others, my, 1);
        s.Key = key;
        s.MyVerdict = mine?.Verdict;
        s.MyFlags = mine?.Flags.ToList() ?? new();
        s.Trust = TrustOf(s);
        return s;
    }

    /// <summary>What your rating adds, counted the way the server counts it (your account's weight included).</summary>
    private RepSummary Estimate(Report r)
    {
        var s = new RepSummary { Key = r.TargetKey };
        var (w, rook) = _settings().Scoring.ReportWeights(r, _myWeight);
        if (r.Verdict == Verdicts.Good) { s.Good = 1; s.WeightedGood = w; s.WeightedRookGood = rook; }
        else
        {
            s.Bad = 1; s.WeightedBad = w; s.WeightedRookBad = rook;
            if (r.Flags.Contains(Flags.RookOnRook)) s.RookOnRook = 1;
            if (r.Flags.Contains(Flags.DcEarly)) s.DcEarly = 1;
            if (r.Flags.Contains(Flags.Toxic)) s.Toxic = 1;
            if (r.Flags.Contains(Flags.Backstab)) s.Backstab = 1;
        }
        return s;
    }

    /// <summary>a + sign * b, field by field (never below zero).</summary>
    private static RepSummary Add(RepSummary a, RepSummary b, int sign)
    {
        static int I(int x) => Math.Max(0, x);
        static double D(double x) => Math.Max(0, Math.Round(x, 6));
        return new RepSummary
        {
            Key = a.Key,
            Good = I(a.Good + sign * b.Good), Bad = I(a.Bad + sign * b.Bad),
            Toxic = I(a.Toxic + sign * b.Toxic), Backstab = I(a.Backstab + sign * b.Backstab),
            RookOnRook = I(a.RookOnRook + sign * b.RookOnRook), DcEarly = I(a.DcEarly + sign * b.DcEarly),
            WeightedGood = D(a.WeightedGood + sign * b.WeightedGood), WeightedBad = D(a.WeightedBad + sign * b.WeightedBad),
            WeightedRookBad = D(a.WeightedRookBad + sign * b.WeightedRookBad), WeightedRookGood = D(a.WeightedRookGood + sign * b.WeightedRookGood),
        };
    }

    public Report? Find(string key)
    {
        lock (_fileGate) return Resolve(key);
    }

    /// <summary>Your report on exactly this runner key. A name without a tag is a different runner from the same name
    /// with a tag (tags are only given to the second and later players to take a name). Call under _fileGate.</summary>
    private Report? Resolve(string key) => Mine().FirstOrDefault(r => r.TargetKey == key);

    /// <summary>Create or overwrite your rating of a runner (one per runner). Saved immediately, synced in the background.</summary>
    public void Submit(Report report)
    {
        if (!Account.SignedIn) return;
        lock (_fileGate)
        {
            // Rating the same runner again replaces the report it already has.
            var prior = Resolve(report.TargetKey);
            var existing = prior == null ? -1 : _reports.IndexOf(prior);
            report.UpdatedUtc = DateTime.UtcNow;
            report.Synced = false;
            report.SteamId = Account.SteamId;
            if (existing >= 0) { report.CreatedUtc = _reports[existing].CreatedUtc; _reports[existing] = report; }
            else _reports.Add(report);
            _pendingDeletes.Remove(report.TargetKey);
            SaveReports();
        }
        Publish(report.TargetKey);
        _ = SyncAsync();
    }

    /// <summary>Removes a rating (user changed their mind back to "skip").</summary>
    public void Retract(string key)
    {
        // Taking a rating back is a change to the shared ratings too: only while signed in.
        if (!Account.SignedIn) return;
        lock (_fileGate)
        {
            var r = Resolve(key);
            if (r != null) _reports.Remove(r);
            _pendingDeletes.Add(key);
            SaveReports();
        }
        Publish(key);
        _ = SyncAsync();
    }

    /// <summary>Your account's weight, asked after signing in (and at start when already signed in).</summary>
    private async Task LoadMyWeightAsync()
    {
        try
        {
            using var resp = await Authed(HttpMethod.Get, $"{Server}/v1/me");
            if (resp is not { IsSuccessStatusCode: true }) return;
            var me = await resp.Content.ReadFromJsonAsync<MeAnswer>(Json);
            if (me?.Weight is double w && w != _myWeight)
            {
                _myWeight = w;
                foreach (var k in _cache.Keys.ToArray()) Publish(k);
            }
        }
        catch (Exception ex) { Log.Error("Account lookup failed", ex); }
    }

    private sealed class MeAnswer { public double? Weight { get; set; } }
    private sealed class WriteAnswer { public RepSummary? Score { get; set; } public RepSummary? Mine { get; set; } public double? Weight { get; set; } }

    /// <summary>
    /// Sends your changes (ratings and take-backs) that the server does not have yet, one at a time and in order.
    /// Retried on every change and at startup, so offline ratings catch up.
    /// </summary>
    public async Task SyncAsync()
    {
        if (Server == null || !Account.SignedIn) return;
        await _sendGate.WaitAsync();
        try
        {
            while (true)
            {
                string? delete;
                Report? put = null;
                lock (_fileGate)
                {
                    delete = _pendingDeletes.FirstOrDefault();
                    if (delete == null) put = Mine().FirstOrDefault(r => !r.Synced);
                }
                if (delete == null && put == null) return;
                string key = delete ?? put!.TargetKey;
                bool sent;
                if (delete != null)
                {
                    sent = await Send(key, () => Authed(HttpMethod.Delete, $"{Server}/v1/reports/{Uri.EscapeDataString(key)}"));
                    if (sent) lock (_fileGate) _pendingDeletes.Remove(key);
                }
                else
                {
                    var r = put!;
                    var body = new { runId = r.RunId, targetName = r.TargetName, verdict = r.Verdict, flags = r.Flags, comment = r.Comment, context = r.Context };
                    sent = await Send(key, () => Authed(HttpMethod.Put, $"{Server}/v1/reports/{Uri.EscapeDataString(key)}", body), r);
                }
                if (!sent) { RetryLater(); return; }   // offline, rate limited or server trouble
            }
        }
        finally { _sendGate.Release(); }
    }

    private int _retryQueued;

    private void RetryLater()
    {
        if (Interlocked.Exchange(ref _retryQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(RetryDelay);
            Interlocked.Exchange(ref _retryQueued, 0);
            await SyncAsync();
        });
    }

    /// <summary>Changes the server has not taken yet (dev tests wait for this to clear).</summary>
    public bool HasPendingChanges
    {
        get { lock (_fileGate) return _pendingDeletes.Count > 0 || Mine().Any(r => !r.Synced); }
    }

    /// <summary>Sends one change and takes the server's new totals from its answer. False if it should be retried later.</summary>
    private async Task<bool> Send(string key, Func<Task<HttpResponseMessage?>> request, Report? report = null)
    {
        lock (_seqGate)
        {
            _writeSeq[key] = _writeSeq.GetValueOrDefault(key) + 1;
            _writing[key] = _writing.GetValueOrDefault(key) + 1;
        }
        bool done = false, answered = false;
        try
        {
            using var resp = await request();
            if (resp == null) return false;   // signed out meanwhile
            if (resp.IsSuccessStatusCode)
            {
                done = true;
                var a = await resp.Content.ReadFromJsonAsync<WriteAnswer>(Json);
                if (a?.Mine != null)
                {
                    answered = true;
                    if (a.Weight is double w) _myWeight = w;
                    _mineOnServer[key] = a.Mine;
                    _others[key] = a.Score == null ? new RepSummary { Key = key } : Add(a.Score, a.Mine, -1);
                }
            }
            else
            {
                Log.Error($"Rating change rejected: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
                // Bad request: this change will never be accepted as is; stop retrying it. Limits or server trouble:
                // try again later.
                done = (int)resp.StatusCode == 400;
            }
        }
        catch (Exception ex) { Log.Error("Rating change failed to send", ex); }
        finally
        {
            lock (_seqGate) _writing[key]--;
        }
        if (done && report != null)
            lock (_fileGate)
                if (_reports.Contains(report)) { report.Synced = true; SaveReports(); }
        Publish(key);
        // A server that does not send totals back: look them up now that the change is in.
        if (done && !answered) _ = RefreshAsync(new[] { key });
        return done;
    }

    private static List<Report> LoadReports()
    {
        try
        {
            if (File.Exists(Paths.ReportsFile))
                return JsonSerializer.Deserialize<List<Report>>(File.ReadAllText(Paths.ReportsFile), Json) ?? new();
        }
        catch (Exception ex) { Log.Error("Could not read reports", ex); }
        return new();
    }

    /// <summary>
    /// Ratings saved under a key with a tag ("NAME#1234") move to the name alone (see NameTools.RunnerKey). Where one
    /// account rated the same name more than once that way, the newest rating is kept, as the server does too.
    /// </summary>
    internal static bool Untag(List<Report> reports)
    {
        if (!reports.Any(r => r.TargetKey.Contains('#'))) return false;
        foreach (var r in reports)
            if (r.TargetKey.IndexOf('#') is var i and > 0) r.TargetKey = r.TargetKey[..i];
        var keep = reports.GroupBy(r => (r.SteamId, r.TargetKey)).Select(g => g.MaxBy(r => r.UpdatedUtc)!).ToHashSet();
        reports.RemoveAll(r => !keep.Contains(r));
        Log.Info("Moved ratings saved under tagged keys to the name alone");
        return true;
    }

    private void SaveReports()
    {
        Directory.CreateDirectory(Paths.DataDir);
        var tmp = Paths.ReportsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_reports, Json));
        File.Move(tmp, Paths.ReportsFile, overwrite: true);
    }
}
