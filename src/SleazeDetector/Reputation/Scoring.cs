namespace SleazeDetector.Reputation;

/// <summary>
/// Tunable reputation rules. The same rules are meant to run server-side once a shared backend exists,
/// so every client shows the same meter for the same runner.
///
/// How a score is formed:
///  1. Each report has a weight: 1 for a normal reporter, <see cref="TrustedReporterWeight"/> for a trusted account
///     (a trusted report counts as if several people had rated). Severe flags (<see cref="RatingCategory.Severe"/>) add <see cref="SevereFlagBonus"/>.
///  2. Bad evidence B = sum of bad weights; good evidence G = sum of good weights.
///  3. Conflicts lean negative: it takes <see cref="GoodPerBad"/> good reports to cancel one bad one.
///     net = B - G / GoodPerBad.  net &gt; 0 means "leans bad"; otherwise positive evidence P = G - B * GoodPerBad.
///  4. Evidence maps to how far the meter leans from neutral via a curve: a gentle lean for the first
///     reports, a clear jump at <see cref="FirmAt"/>, fully bad/good at <see cref="FullAt"/>.
///  5. Trust = 0.5 -/+ 0.5 * lean   (0 = fully sleazy, 1 = fully trustworthy, 0.5 = neutral).
///
/// Rook ratings are a separate channel, used only while you play Rook: "Rook on Rook violence" (bad) and "Nice to
/// Rooks" (good), each weighing <see cref="RookOnRookWeight"/> times the reporter weight. They balance each other as in
/// step 3 (<see cref="GoodPerBad"/> nice ratings per violent one). If the Rook channel comes out negative, the meter
/// shows negative while you play Rook, whatever the general ratings say; if it comes out positive, that adds to the
/// good evidence. A rating flagged only with a Rook flag counts nothing for viewers not playing Rook.
/// </summary>
public sealed class ScoringRules
{
    /// <summary>Weight of a report from an account on the trusted list ("counts like N people").</summary>
    public double TrustedReporterWeight { get; set; } = 3.0;
    /// <summary>Extra weight for a bad report with a severe flag (see <see cref="RatingCategories"/>).</summary>
    public double SevereFlagBonus { get; set; } = 0.5;
    /// <summary>Weight of a Rook on Rook or Nice to Rooks report (times the reporter weight) while you are playing Rook.</summary>
    public double RookOnRookWeight { get; set; } = 2.0;
    /// <summary>Good reports needed to cancel out one bad report.</summary>
    public double GoodPerBad { get; set; } = 2.0;
    /// <summary>Lean added per report before reaching <see cref="FirmAt"/> (the "slowly leans" phase).</summary>
    public double LeanPerReport { get; set; } = 0.12;
    /// <summary>Evidence at which the lean jumps to <see cref="FirmLean"/>.</summary>
    public double FirmAt { get; set; } = 3;
    public double FirmLean { get; set; } = 0.55;
    /// <summary>Evidence at which the meter is pinned fully bad (or fully good).</summary>
    public double FullAt { get; set; } = 5;
    /// <summary>Steam IDs (64-bit) whose reports get <see cref="TrustedReporterWeight"/>. Enforced by the server.</summary>
    public List<string> TrustedSteamIds { get; set; } = new();

    public double Lean(double evidence)
    {
        if (evidence <= 0) return 0;
        double soft = LeanPerReport * Math.Max(0, FirmAt - 1);           // lean at FirmAt - 1
        if (evidence <= FirmAt - 1) return LeanPerReport * evidence;
        if (evidence <= FirmAt) return soft + (FirmLean - soft) * (evidence - (FirmAt - 1));
        if (evidence >= FullAt) return 1.0;
        return FirmLean + (1.0 - FirmLean) * (evidence - FirmAt) / Math.Max(0.0001, FullAt - FirmAt);
    }

    /// <returns>0..1 trust, or null with no evidence.</returns>
    /// <param name="weightedRookBad">Rook on Rook evidence, used only when <paramref name="playingRook"/>.</param>
    /// <param name="weightedRookGood">Nice to Rooks evidence, used only when <paramref name="playingRook"/>.</param>
    public double? Trust(double weightedGood, double weightedBad, double weightedRookBad = 0, double weightedRookGood = 0,
                         bool playingRook = false)
    {
        if (playingRook && (weightedRookBad > 0 || weightedRookGood > 0))
        {
            double rookNet = weightedRookBad - weightedRookGood / GoodPerBad;
            // Violent to Rooks on balance: negative while you play Rook, with any general bad evidence added.
            if (rookNet > 0) return 0.5 - 0.5 * Lean(rookNet + Math.Max(0, weightedBad - weightedGood / GoodPerBad));
            weightedGood += weightedRookGood - weightedRookBad * GoodPerBad;
        }
        if (weightedGood <= 0 && weightedBad <= 0) return null;
        double net = weightedBad - weightedGood / GoodPerBad;
        if (net > 0) return 0.5 - 0.5 * Lean(net);
        double positive = weightedGood - weightedBad * GoodPerBad;
        return 0.5 + 0.5 * Lean(positive);
    }

    /// <summary>
    /// A report's evidence. General: what it adds for everyone, to good or bad by its verdict. Rook: what its Rook flag
    /// (Rook on Rook for a bad report, Nice to Rooks for a good one) adds to the Rook channel. A report flagged only
    /// with the Rook flag has no general weight.
    /// </summary>
    /// <param name="w">How many people the reporter counts as (1 for a normal account).</param>
    public (double General, double Rook) ReportWeights(Report r, double w)
    {
        string rookFlag = r.Verdict == Verdicts.Bad ? Flags.RookOnRook : Flags.NiceToRook;
        bool rook = r.Flags.Contains(rookFlag);
        bool otherFlags = r.Flags.Any(f => f != rookFlag);
        double general = rook && !otherFlags ? 0 : w;
        if (general > 0 && r.Verdict == Verdicts.Bad && r.Flags.Any(RatingCategories.IsSevere)) general += SevereFlagBonus;
        return (general, rook ? w * RookOnRookWeight : 0);
    }
}
