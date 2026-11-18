using System.Text;

namespace SleazeDetector.Detection;

/// <summary>
/// OCR on a stylised game font is noisy (RUNNER -> RLNNER, NAME144 -> NAMEi44, 0 drawn with a slash).
/// Names are therefore compared by a "match key" that folds look-alike glyphs together, plus a small
/// edit-distance allowance. The server keys reputation by the same match key, so different misreads
/// of one runner land on the same record.
/// </summary>
public static class NameTools
{
    // HUD vocabulary that must never be mistaken for a runner name.
    private static readonly string[] HudWords =
    {
        "DOWNED", "PINGED", "REVIVING", "REVIVED", "REVIVE", "DEPLOYS", "DEPLOYED", "ELIMINATED", "DMG", "CARGO",
        "SPECTATING", "NEURAL", "LINK", "SEVERED", "SYSTEM", "DEFENDER", "SECURITY", "CLEARANCE", "LEVEL", "LOOTING",
        "FINISHER", "ASSIST", "RUNNER", "RUNNERS", "DOWN", "ELIM", "SHIELD", "CHARGE", "STEALTH", "SQUAD", "PROXIMITY",
        "CHAT", "BACK", "SHOW", "ITEM", "VALUE", "LOADOUT", "BACKPACK", "EQUIPMENT", "CORES", "IMPLANTS", "ABANDONED",
        "GEAR", "ACTIVE", "WEAPON", "HEALING", "SLOWED", "TERMINAL", "TERMINALS", "EXFIL", "EXFILTRATED", "EXTRACT",
        "CONTROL", "ARSENAL", "RANSACK", "HACK", "LOOT", "TAGS", "SWITCH", "LEAVE", "CONTINUE", "RECAP", "REPORT",
        "BLEEDING", "BLEEDOUT", "SCANNER", "DRONE", "SENTRY", "TURRET", "PACK", "KILLED", "TEAMMATE", "ENEMY", "HOSTILE",
        "INTERFACE", "OFFLINE", "SHUTTING", "TERMINATING", "NORTH", "SOUTH", "EAST", "WEST", "UESC", "CLOSE", "DOOR",
        "OPEN", "SEARCH", "PICK", "DROP", "RELOAD", "AMMO", "ROUNDS", "LIGHT", "HEAVY", "MEDIUM", "ENERGY", "VOLT",
        // Video player chrome when testing with recordings ("Marathon 2026.11.13 - 22.32.33.09.DVR.mp4", "3:46").
        "MARATHON", "DVR", "MP4", "MOVIES", "PLAYER",
    };

    private static readonly HashSet<string> HudKeys = HudWords.Select(MatchKey).ToHashSet();

    /// <summary>Single-token convenience wrapper over <see cref="CleanName"/>.</summary>
    public static string? Clean(string token) => CleanName(Words(token));

    /// <summary>A read split into its words: the spaces are part of the name ("UESC BREACH RECRUIT").</summary>
    public static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Builds a runner name from the OCR words of one HUD segment, or null if it can't be a name.
    /// Handles multi-word names ("NOVA 42 FOX"), Bungie-style tags ("RUNNER#1234"), the squad badge
    /// that precedes squad names ("A1"), and stray single glyphs read from icons.
    /// </summary>
    /// <param name="hudFilter">Reject words of the HUD itself ("PINGED", "DEPLOYS"). Only for text read from white /
    /// mixed regions: names found by their red or lime colour can't be HUD words, and filtering them would let a
    /// player hide by picking a HUD-like name.</param>
    /// <param name="exact">The text came from the learned reader, which tells digits from letters: keep them as read
    /// (no O/0, I/1 repair, which would turn "M1KE" into "MIKE").</param>
    public static string? CleanName(IEnumerable<string> words, bool hudFilter = true, bool exact = false)
    {
        var raw = words.ToList();
        // Times and dates ("3:46", "2026.11.13", "22.32.33.09") are never names.
        if (raw.Any(w => System.Text.RegularExpressions.Regex.IsMatch(w, @"\d[.:]\d"))) return null;
        var parts = raw.Select(StripToken).Where(t => t.Length > 0).ToList();
        while (parts.Count > 1 && parts[0].Length <= 2) parts.RemoveAt(0);          // badge / icon glyph
        while (parts.Count > 1 && parts[^1].Length == 1) parts.RemoveAt(parts.Count - 1);
        if (parts.Count == 0 || parts.Count > 4) return null;
        if (hudFilter && parts.Any(p => p.Length >= 5 && IsHudWord(p))) return null; // "DEPLOYS", "PINGED"...
        var s = string.Join(' ', parts);
        if (s.Length < 3 || s.Length > 40) return null;
        if (s.Count(char.IsAsciiLetter) < 2) return null;                            // "152", "29/30"
        if (hudFilter && parts.Count == 1 && IsHudWord(s)) return null;
        return exact ? s : LettersInsideWords(s);
    }

    /// <summary>
    /// At HUD size the font's O / 0, I / 1, S / 5 and B / 8 differ by a pixel or two, so reads mix them up
    /// ("BL000Y", "RUNN0R"). Digits sitting between letters inside a word are taken as the letters they
    /// look like; digit groups at a word's start or end ("PLAYER123", "NOVA 42") and the tag after '#' are kept.
    /// Matching already treats these as equal; this is for the name people see.
    /// </summary>
    public static string LettersInsideWords(string s)
    {
        int hash = s.IndexOf('#');
        int end = hash >= 0 ? hash : s.Length;
        var c = s.ToCharArray();
        for (int i = 0; i < end;)
        {
            if (!char.IsAsciiDigit(c[i])) { i++; continue; }
            int j = i;
            while (j < end && char.IsAsciiDigit(c[j])) j++;
            bool inside = i > 0 && j < end && char.IsAsciiLetter(c[i - 1]) && char.IsAsciiLetter(c[j]);
            if (inside && c[i..j].All(d => d is '0' or '1' or '5' or '8'))
                for (int k = i; k < j; k++) c[k] = c[k] switch { '0' => 'O', '1' => 'I', '5' => 'S', _ => 'B' };
            i = j;
        }
        return new string(c);
    }

    private static string StripToken(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            if (c is '\u00D8' or '\u00F8') sb.Append('0');                        // slashed zero glyph
            else if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '#' or '*') sb.Append(c);   // '*': censored names
        }
        return sb.ToString().Trim('-', '.', '_', '#');
    }

    /// <summary>
    /// Prox chat shows "NAME#1234". The tag is what tells the many CHRISes apart, so it is part of a runner's identity.
    /// OCR reads it badly ('#' becomes 1/i/t/r, digits become letters), so the tail is decoded as "#" plus four
    /// digits with look-alike letters mapped to digits. Positions that can't be decoded are '?', and the runner's
    /// tag is then voted per digit across many reads.
    /// </summary>
    public static (string Base, string? Tag) SplitTag(string raw)
    {
        int i = raw.IndexOf('#');
        if (i >= 3)
        {
            var tail = new string(raw[(i + 1)..].Where(c => c != ' ').Take(4).ToArray());
            return (raw[..i].Trim(), tail.Length == 4 ? tail : null);
        }
        // '#' misread as another glyph: the last five characters are "#dddd" if at least three decode as digits.
        if (!raw.Contains(' ') && raw.Length >= 9)
        {
            var tail = raw[^4..];
            if (tail.Count(c => DigitVotes(c).Length > 0) >= 3) return (raw[..^5], tail);
        }
        return (raw, null);
    }

    /// <summary>
    /// Which digits an OCR'd tag glyph may really be, with weights. In Marathon's HUD font a 6 is often read as
    /// 'B' or 'e', and a 0 as 'e' or a slashed O, so ambiguous glyphs split their vote; across many reads the true
    /// digit wins.
    /// </summary>
    public static (char Digit, double Weight)[] DigitVotes(char c) => c switch
    {
        >= '0' and <= '9' => [(c, 1)],
        'O' or 'o' or 'D' or 'Q' or 'U' or 'C' or 'c' or '\u00D8' or '\u00F8' => [('0', 1)],
        'I' or 'i' or 'L' or 'l' or 'J' or 'j' or 'T' or 't' or 'F' or 'f' or '!' or '|' => [('1', 1)],
        'Z' or 'z' => [('2', 1)],
        'A' => [('4', 1)],
        'S' or 's' => [('5', 1)],
        'G' or 'b' or '\u00C5' or '\u00E5' => [('6', 1)],
        'e' or 'E' => [('6', 0.6), ('0', 0.4)],
        'B' => [('8', 0.6), ('6', 0.4)],
        'g' or 'R' => [('8', 1)],
        'P' or 'q' => [('9', 1)],
        _ => [],
    };

    /// <summary>Single best digit per glyph ('?' if none); used to compare one read against a runner's tag.</summary>
    public static string DecodeTag(string tail) =>
        new string(tail.Select(c => DigitVotes(c) is { Length: > 0 } v ? v.MaxBy(x => x.Weight).Digit : '?').ToArray());

    /// <summary>Whole tags that differ in two or more known digits belong to different runners (one digit can be OCR noise).</summary>
    public static bool TagsConflict(string? a, string? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != '?' && b[i] != '?' && a[i] != b[i]) diff++;
        return diff >= 2;
    }

    /// <summary>Reputation key: look-alike-folded base name plus the exact tag digits ("0UAR12W01F#0816").</summary>
    public static string RunnerKey(string name)
    {
        // Only an explicit '#' marks a tag here; the "last five characters" guess is for raw prox reads only.
        int i = name.IndexOf('#');
        var (b, tail) = i >= 3 ? SplitTag(name) : (name, null);
        var tag = tail == null ? null : DecodeTag(tail);
        return tag != null && !tag.Contains('?') ? $"{MatchKey(b)}#{tag}" : MatchKey(b);
    }

    /// <summary>In-game things that can be pinged or appear in the feed but are not runners ("UESC GHOST").</summary>
    /// <param name="partial">Also match a single word of a multi-word entry ("GHOST" of "UESC GHOST"): used on ping
    /// lines, where a fading or partly covered target often shows only one of its words.</param>
    public static bool IsIgnored(string name, IEnumerable<string> ignored, bool partial = false)
    {
        var k = MatchKey(name);
        foreach (var i in ignored)
        {
            var ik = MatchKey(i);
            if (ik.Length == 0) continue;
            if (k == ik || (ik.Length >= 5 && Levenshtein(k, ik) <= Math.Max(1, ik.Length / 4)) || (k.Length >= ik.Length && k.Contains(ik))) return true;
            if (partial)
                foreach (var word in i.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(MatchKey).Where(w => w.Length >= 4))
                    if (k == word || (word.Length >= 5 && Levenshtein(k, word) <= 1)) return true;
        }
        return false;
    }

    public static bool IsHudWord(string s)
    {
        var k = MatchKey(s);
        if (HudKeys.Contains(k)) return true;
        // Misread HUD words are caught with one wrong letter ("PINGEO"); two only for long words. Looser than this
        // rejects real names that happen to resemble a HUD word ("KILLER" is one letter from "KILLED").
        int allowed = k.Length >= 9 ? 2 : k.Length >= 5 ? 1 : 0;
        if (allowed > 0)
            foreach (var hk in HudKeys)
                if (Math.Abs(hk.Length - k.Length) <= allowed && Levenshtein(hk, k) <= allowed) return true;
        return false;
    }

    /// <summary>Uppercase and fold glyphs the OCR confuses on this font.</summary>
    public static string MatchKey(string name)
    {
        var sb = new StringBuilder(name.Length);
        // W in this font is often read as two strokes: "VV", "IV", "VI" (WOLF -> IVOLF).
        var up = name.ToUpperInvariant().Replace("VV", "W").Replace("IV", "W").Replace("VI", "W");
        foreach (var raw in up)
        {
            sb.Append(raw switch
            {
                'O' or '\u00D8' or 'Q' or 'D' => '0',
                'I' or 'L' or 'J' or '!' or '|' or 'T' => '1',
                'S' => '5',
                'B' => '8',
                'Z' => '2',
                'G' => '6',
                'U' or 'V' => 'U',
                'M' or 'N' or 'H' => 'N',
                // Censored names show stars ("*****NAME69"); everyone sees the same stars, so they are identity.
                '*' => '*',
                // '_' is dropped like spaces: OCR reads "NAME_123" as "NAME 123".
                _ when char.IsAsciiLetterOrDigit(raw) => raw,
                _ => '\0',
            });
        }
        return sb.ToString().Replace("\0", "");
    }

    public static bool Similar(string a, string b)
    {
        string ka = MatchKey(a), kb = MatchKey(b);
        if (ka == kb) return true;
        int len = Math.Min(ka.Length, kb.Length);
        int allowed = len >= 12 ? 2 : len >= 5 ? 1 : 0;
        return allowed > 0 && Math.Abs(ka.Length - kb.Length) <= allowed && Levenshtein(ka, kb) <= allowed;
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
