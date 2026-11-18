namespace SleazeDetector.Reputation;

/// <summary>One checkbox on a rating: a flag id (stored with the rating) and the label shown for it.</summary>
/// <param name="Bad">Offered with a bad rating (otherwise with a good one).</param>
/// <param name="TeammateOnly">Only offered for squadmates.</param>
/// <param name="Severe">Adds <see cref="ScoringRules.SevereFlagBonus"/> to the rating's weight.</param>
public sealed record RatingCategory(string Id, string Label, bool Bad, string? Tip = null, bool TeammateOnly = false, bool Severe = false);

/// <summary>
/// The categories a rating can be flagged with. To add or remove one, edit this list and the matching lists in
/// server/wrangler.toml (BAD_FLAGS, GOOD_FLAGS, SEVERE_FLAGS); the server ignores flags it does not know.
/// </summary>
public static class RatingCategories
{
    public static readonly RatingCategory[] All =
    {
        new(Flags.Backstab, "Backstabbed me", Bad: true, Severe: true),
        new(Flags.Toxic, "Toxic", Bad: true),
        new(Flags.RookOnRook, "Rook on Rook violence", Bad: true),
        new(Flags.DcEarly, "DCs early", Bad: true,
            Tip: "Disconnects after being downed while the team is still alive with a chance of success", TeammateOnly: true),
        new(Flags.KeptWord, "Kept their word", Bad: false),
        new(Flags.Friendly, "Friendly", Bad: false),
        new(Flags.NiceToRook, "Nice to Rooks as Rook", Bad: false),
    };

    public static bool IsSevere(string id) => All.Any(c => c.Id == id && c.Severe);
}
