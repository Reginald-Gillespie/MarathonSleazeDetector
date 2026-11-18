using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SleazeDetector.Core;
using SleazeDetector.Reputation;

namespace SleazeDetector.Dev;

/// <summary>
/// Dev tool: drives the rating code against a test copy of the ratings server (wrangler dev with a throwaway local
/// database) and checks that what the meter shows always matches what was clicked and, once everything is sent, what
/// the server holds. Uses a temporary data folder, so your own sign-in and ratings are never touched.
///   SleazeDetector --rep-test http://127.0.0.1:8799 ADMIN_TOKEN OTHER_TOKEN out.txt
/// The admin account's ratings count as 5 people on the server; the other account is a normal one (1).
/// </summary>
public static class RepTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly List<string> Out = new();
    private static int _fails;

    public static async Task<int> RunAsync(string[] args)
    {
        string server = args[0], adminToken = args[1], otherToken = args[2], outFile = args[3];
        var dir = Path.Combine(Path.GetTempPath(), "sleaze-rep-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Paths.UseDataDir(dir);
        File.WriteAllBytes(Path.Combine(dir, "account.dat"),
            ProtectedData.Protect(Encoding.UTF8.GetBytes($"76561190000000001\n{adminToken}"), null, DataProtectionScope.CurrentUser));
        ReputationService.ServerOverride = server;
        ReputationService.RetryDelay = TimeSpan.FromSeconds(2);
        var settings = new AppSettings();
        var http = new HttpClient();
        var rnd = new Random(7);
        try
        {
            var rep = new ReputationService(() => settings);
            await Task.Delay(1500);   // the account weight lookup
            var run = Guid.NewGuid();
            string tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

            // 1. One bad rating from the admin: 5 people's worth, shown at once and still after the server answers.
            string k1 = $"ONEBAD{tag}";
            rep.Peek(k1);
            await Task.Delay(500);
            Check("unrated runner shows nothing", rep.Peek(k1)?.Trust == null);
            rep.Submit(Rep(run, k1, Verdicts.Bad));
            Expect(rep, k1, "right after Bad", good: 0, bad: 5);
            await Settle(rep);
            Expect(rep, k1, "after the server answered", good: 0, bad: 5);
            await MatchServer(rep, http, server, k1, "single bad");

            // 2. Toggle storm: random Good / Bad / take back, 0-40 ms apart, while lookups run all the time and a
            //    normal account has rated the runner bad. After each click the meter must show exactly the other
            //    rating plus the click, right away and still just before the next click.
            string k2 = $"STORM{tag}";
            await Put(http, server, otherToken, k2, Verdicts.Bad);
            await rep.RefreshAsync(new[] { k2 });
            using var stop = new CancellationTokenSource();
            var lookups = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested) { await rep.RefreshAsync(new[] { k2 }); await Task.Delay(rnd.Next(5, 25)); }
            });
            string? mine = null;
            int clickFails = 0;
            for (int i = 0; i < 60; i++)
            {
                int pick = rnd.Next(3);
                string? next = pick == 0 ? Verdicts.Good : pick == 1 ? Verdicts.Bad : null;
                if (next == null) rep.Retract(k2); else rep.Submit(Rep(run, k2, next));
                mine = next;
                double eg = mine == Verdicts.Good ? 5 : 0, eb = 1 + (mine == Verdicts.Bad ? 5 : 0);
                if (!Same(rep.Peek(k2), eg, eb)) clickFails++;
                await Task.Delay(rnd.Next(0, 40));
                if (!Same(rep.Peek(k2), eg, eb))
                {
                    clickFails++;
                    Out.Add($"    click {i} ({next ?? "take back"}): shows good {rep.Peek(k2)?.WeightedGood} bad {rep.Peek(k2)?.WeightedBad}, expected {eg} / {eb}");
                }
            }
            stop.Cancel();
            await lookups;
            Check("storm: meter matched every click, at once and until the next click", clickFails == 0, $"{clickFails} mismatches");
            await Settle(rep);
            await MatchServer(rep, http, server, k2, "storm");
            var final = await Totals(http, server, k2);
            double fg = mine == Verdicts.Good ? 5 : 0, fb = 1 + (mine == Verdicts.Bad ? 5 : 0);
            Check("storm: server holds the last click", final != null && final.WeightedGood == fg && final.WeightedBad == fb,
                $"server good {final?.WeightedGood} bad {final?.WeightedBad}, last click {mine ?? "take back"}");

            // 3. Someone else changes their rating while you toggle: everything agrees once it settles.
            string k3 = $"BOTH{tag}";
            var others = Task.Run(async () =>
            {
                foreach (var v in new[] { Verdicts.Bad, Verdicts.Good, Verdicts.Bad, Verdicts.Good }) { await Put(http, server, otherToken, k3, v); await Task.Delay(30); }
            });
            for (int i = 0; i < 20; i++)
            {
                if (i % 3 == 2) rep.Retract(k3); else rep.Submit(Rep(run, k3, i % 2 == 0 ? Verdicts.Bad : Verdicts.Good));
                await Task.Delay(rnd.Next(0, 30));
            }
            await others;
            await Settle(rep);
            await rep.RefreshAsync(new[] { k3 });
            await MatchServer(rep, http, server, k3, "both raters changing");

            // 4. Offline: changes made while the server cannot be reached show at once and go up when it is back.
            string k4 = $"OFFLINE{tag}";
            ReputationService.ServerOverride = "http://127.0.0.1:1";
            rep.Submit(Rep(run, k4, Verdicts.Bad));
            Expect(rep, k4, "offline Bad", good: 0, bad: 5);
            rep.Submit(Rep(run, k4, Verdicts.Good));
            Expect(rep, k4, "offline Good", good: 5, bad: 0);
            rep.Retract(k4);
            Expect(rep, k4, "offline take back", good: 0, bad: 0);
            rep.Submit(Rep(run, k4, Verdicts.Bad, Flags.Backstab));
            Expect(rep, k4, "offline Bad + backstab", good: 0, bad: 5.5);
            await Task.Delay(500);
            ReputationService.ServerOverride = server;
            await Settle(rep);   // the retry timer sends it
            await MatchServer(rep, http, server, k4, "offline catch-up");

            // 5. Taken back while offline, then the server is back: the server forgets the rating.
            string k5 = $"GONE{tag}";
            rep.Submit(Rep(run, k5, Verdicts.Bad));
            await Settle(rep);
            ReputationService.ServerOverride = "http://127.0.0.1:1";
            rep.Retract(k5);
            Expect(rep, k5, "offline take back of a sent rating", good: 0, bad: 0);
            ReputationService.ServerOverride = server;
            await Settle(rep);
            var gone = await Totals(http, server, k5);
            Check("take back reached the server", gone == null || (gone.WeightedBad == 0 && gone.WeightedGood == 0), $"server bad {gone?.WeightedBad}");

            // 6. App restart: a new instance (reading the saved ratings) shows the same as the server.
            var rep2 = new ReputationService(() => settings);
            await Task.Delay(1500);
            foreach (var k in new[] { k1, k2, k3, k4 })
            {
                rep2.Peek(k);
                await Task.Delay(800);
                await MatchServer(rep2, http, server, k, $"after restart ({k[..^tag.Length]})");
            }
            // A click after the restart still adds up.
            rep2.Submit(Rep(run, k1, Verdicts.Good));
            Expect(rep2, k1, "Good after restart", good: 5, bad: 0);
            await Settle(rep2);
            await MatchServer(rep2, http, server, k1, "Good after restart, sent");

            // 7. Signed out: no ratings of your own, nothing can be rated or taken back, the server keeps yours.
            rep2.Account.SignOut();
            Check("signed out: your rating is not shown as yours", rep2.Peek(k1)?.MyVerdict == null && rep2.MyReports.Count == 0);
            rep2.Submit(Rep(run, k1, Verdicts.Bad));
            rep2.Retract(k1);
            Check("signed out: rating and taking back do nothing", rep2.MyReports.Count == 0 && !rep2.HasPendingChanges);
            var afterOut = await Totals(http, server, k1);
            Check("signed out: the server still has your rating", afterOut != null && afterOut.WeightedGood == 5, $"server good {afterOut?.WeightedGood}");

            // 8. Another account signs in on this PC: it sees none of the first account's ratings and sends none.
            string k8 = $"MINEONLY{tag}";
            var repA = new ReputationService(() => settings);
            repA.Submit(Rep(run, k8, Verdicts.Bad));   // signed out: refused
            File.WriteAllBytes(Path.Combine(dir, "account.dat"),
                ProtectedData.Protect(Encoding.UTF8.GetBytes($"76561190000000002\n{otherToken}"), null, DataProtectionScope.CurrentUser));
            var repB = new ReputationService(() => settings);
            await Task.Delay(1500);
            Check("other account: sees none of the first account's ratings", repB.MyReports.Count == 0 && repB.Find(k1) == null);
            await Settle(repB);
            var stillGood = await Totals(http, server, k1);
            Check("other account: sent nothing of the first account's", stillGood != null && stillGood.WeightedGood == 5 && stillGood.Good == 1,
                $"server good {stillGood?.WeightedGood} ({stillGood?.Good} ratings)");
            repB.Submit(Rep(run, k8, Verdicts.Bad));
            await Settle(repB);
            Expect(repB, k8, "other account: its own rating counts as a normal account", good: 0, bad: 1);

            // 9. The first account signs back in: its ratings are its own again, the other account's are not.
            File.WriteAllBytes(Path.Combine(dir, "account.dat"),
                ProtectedData.Protect(Encoding.UTF8.GetBytes($"76561190000000001\n{adminToken}"), null, DataProtectionScope.CurrentUser));
            var repC = new ReputationService(() => settings);
            await Task.Delay(1500);
            Check("first account again: its rating is back", repC.Find(k1)?.Verdict == Verdicts.Good && repC.Find(k8) == null);
            repC.Peek(k8);
            await Task.Delay(800);
            await MatchServer(repC, http, server, k8, "first account again, other account's runner");

            // 10. Rook on Rook: a bad rating with only that box counts for nobody but Rook players; with another box
            //     it counts for everyone.
            string k10 = $"ROOKER{tag}";
            repC.Submit(Rep(run, k10, Verdicts.Bad, Flags.RookOnRook));
            await Settle(repC);
            var rookOnly = await Totals(http, server, k10);
            Check("rook only: no general weight on the server", rookOnly != null && rookOnly.WeightedBad == 0 && rookOnly.WeightedRookBad > 0,
                $"server bad {rookOnly?.WeightedBad} rook {rookOnly?.WeightedRookBad}");
            repC.PlayingRook = false;
            Check("rook only: meter unrated when you are not playing Rook", repC.Peek(k10)?.Trust == null, $"trust {repC.Peek(k10)?.Trust}");
            repC.PlayingRook = true;
            Check("rook only: meter bad while you play Rook", repC.Peek(k10)?.Trust is < 0.5, $"trust {repC.Peek(k10)?.Trust}");
            repC.PlayingRook = false;
            repC.Submit(Rep(run, k10, Verdicts.Bad, Flags.RookOnRook, Flags.Toxic));
            await Settle(repC);
            Check("rook + toxic: meter bad for everyone", repC.Peek(k10)?.Trust is < 0.5, $"trust {repC.Peek(k10)?.Trust}");
            await MatchServer(repC, http, server, k10, "rook + toxic");
        }
        catch (Exception ex) { _fails++; Out.Add("CRASH " + ex); }
        Out.Add(_fails == 0 ? "ALL PASSED" : $"{_fails} FAILED");
        File.WriteAllLines(outFile, Out);
        try { Directory.Delete(dir, true); } catch { }
        return _fails == 0 ? 0 : 1;
    }

    private static Report Rep(Guid run, string key, string verdict, params string[] flags) => new()
    {
        RunId = run, TargetKey = key, TargetName = key, Verdict = verdict, Flags = flags.ToList(),
    };

    private static bool Same(RepSummary? s, double good, double bad) =>
        s != null && Math.Abs(s.WeightedGood - good) < 0.01 && Math.Abs(s.WeightedBad - bad) < 0.01;

    private static void Expect(ReputationService rep, string key, string what, double good, double bad)
    {
        var s = rep.Peek(key);
        Check(what, Same(s, good, bad) || (good == 0 && bad == 0 && s?.Trust == null),
            $"shows good {s?.WeightedGood} bad {s?.WeightedBad}, expected {good} / {bad}");
    }

    private static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Out.Add($"{(ok ? "PASS" : "FAIL")} {what}{(ok || detail.Length == 0 ? "" : ": " + detail)}");
    }

    private static async Task Settle(ReputationService rep)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            await rep.SyncAsync();
            if (!rep.HasPendingChanges) return;
            await Task.Delay(300);
        }
        Check("changes were all sent within 90 s", false);
    }

    /// <summary>The meter (as it is now, and after a fresh lookup) shows exactly the server's totals.</summary>
    private static async Task MatchServer(ReputationService rep, HttpClient http, string server, string key, string what)
    {
        var t = await Totals(http, server, key);
        double g = t?.WeightedGood ?? 0, b = t?.WeightedBad ?? 0;
        var now = rep.Peek(key);
        Check($"{what}: meter matches server", (g == 0 && b == 0 && now?.Trust == null) || Same(now, g, b),
            $"meter good {now?.WeightedGood} bad {now?.WeightedBad}, server {g} / {b}");
        await rep.RefreshAsync(new[] { key });
        var after = rep.Peek(key);
        Check($"{what}: meter matches server after a lookup", (g == 0 && b == 0 && after?.Trust == null) || Same(after, g, b),
            $"meter good {after?.WeightedGood} bad {after?.WeightedBad}, server {g} / {b}");
    }

    private static async Task<RepSummary?> Totals(HttpClient http, string server, string key)
    {
        var list = await http.GetFromJsonAsync<List<RepSummary>>($"{server}/v1/reputation?keys={Uri.EscapeDataString(key)}", Json);
        return list?.FirstOrDefault();
    }

    private static async Task Put(HttpClient http, string server, string token, string key, string verdict)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{server}/v1/reports/{Uri.EscapeDataString(key)}")
        {
            Content = JsonContent.Create(new { verdict, targetName = key, flags = Array.Empty<string>() }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) Out.Add($"    other account's rating refused: {(int)resp.StatusCode}");
    }
}
