namespace SleazeDetector.Reputation;

public static class Verdicts
{
    public const string Good = "good", Bad = "bad";
}

public static class Flags
{
    // Bad
    public const string Toxic = "toxic", Backstab = "backstab";
    /// <summary>Killed a Rook while playing Rook. Only counts against them while you are playing Rook yourself.</summary>
    public const string RookOnRook = "rook_on_rook";
    /// <summary>A squadmate who disconnected after being downed while the team could still have won. Teammates only.</summary>
    public const string DcEarly = "dc_early";
    // Good
    public const string KeptWord = "kept_word", Friendly = "friendly";
    /// <summary>Treated you well while you were playing Rook. Only counts for viewers playing Rook.</summary>
    public const string NiceToRook = "nice_to_rook";
}

/// <summary>
/// Your rating of one runner. Each person rates a runner once: rating them again (in a later run, or
/// changing your mind) overwrites your previous report instead of adding another one.
/// </summary>
public sealed class Report
{
    public string TargetKey { get; set; } = "";
    public string TargetName { get; set; } = "";
    /// <summary>The run in which the rating was last changed.</summary>
    public Guid RunId { get; set; }
    public string Verdict { get; set; } = Verdicts.Bad;
    public List<string> Flags { get; set; } = new();
    public string Comment { get; set; } = "";
    public string Context { get; set; } = "";
    /// <summary>The Steam account that made the rating. Only the account signed in now sees and sends its own.</summary>
    public string? SteamId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public bool Synced { get; set; }
}

/// <summary>Aggregated reputation for a runner (community counts from the server, or your own rating offline).</summary>
public sealed class RepSummary
{
    public string Key { get; set; } = "";
    public int Good { get; set; }
    public int Bad { get; set; }
    public int Toxic { get; set; }
    public int Backstab { get; set; }
    public int RookOnRook { get; set; }
    public int DcEarly { get; set; }
    /// <summary>Evidence after reporter weights (trusted accounts count extra). Computed by the server when online.</summary>
    public double WeightedGood { get; set; }
    public double WeightedBad { get; set; }
    /// <summary>Rook on Rook evidence, used while you play Rook. See <see cref="ScoringRules.ReportWeights"/>.</summary>
    public double WeightedRookBad { get; set; }
    /// <summary>Nice to Rooks evidence, used while you play Rook. See <see cref="ScoringRules.ReportWeights"/>.</summary>
    public double WeightedRookGood { get; set; }
    /// <summary>0 (sleazy) .. 1 (trustworthy), null if unrated, for the shell you are playing now. See <see cref="ScoringRules"/>.</summary>
    public double? Trust { get; set; }
    /// <summary>Your own verdict on this runner, if any.</summary>
    public string? MyVerdict { get; set; }
    public List<string> MyFlags { get; set; } = new();

    public int Total => Good + Bad;
}
