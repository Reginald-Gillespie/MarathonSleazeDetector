using System.Text.Json;
using System.Text.Json.Serialization;
using SleazeDetector.Reputation;

namespace SleazeDetector.Core;

/// <summary>A screen region, as fractions (0..1) of the game's client area so it works at any resolution.</summary>
public sealed class Zone
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    /// <summary>How often to OCR this zone.</summary>
    public int IntervalMs { get; set; } = 500;
    /// <summary>Sightings needed before a name from this zone counts as a real runner (filters OCR junk).</summary>
    public int MinSightings { get; set; } = 2;
    /// <summary>Upscale factor before OCR. Small text needs more.</summary>
    public int Scale { get; set; } = 2;
    /// <summary>Preprocessing passes to OCR (BrightText, Raw, ColorText). Results are merged.</summary>
    public string[] Passes { get; set; } = { "BrightText", "Raw" };
}

public sealed class AppSettings
{
    public const string FeedZone = "feed", SelfZone = "self", DeathZone = "death", EndZone = "endscreen", ProxZone = "prox";
    private const int CurrentVersion = 5;

    public int SettingsVersion { get; set; } = CurrentVersion;
    public bool StartWithWindows { get; set; } = true;

    /// <summary>In-game things that show up like runner names (pings, feed) but are not players. Never tracked or rated.</summary>
    public List<string> IgnoredNames { get; set; } = DefaultIgnoredNames.ToList();

    /// <summary>
    /// Bots that show up in the kill feed and in pings; they never become runners (NameTools.IsKnownBot). On ping
    /// lines a single word of one also counts ("GRENADIER" with the "UESC" hidden under an icon).
    /// </summary>
    public static readonly string[] DefaultIgnoredNames =
    {
        "UESC RECRUIT", "UESC BREACH RECRUIT", "UESC ASSAULT TROOPER", "UESC ASSAULT CMDR", "UESC GRENADIER",
        "UESC GRENADIER CMDR", "UESC GHOST", "UESC GHOST CMDR", "UESC TURRET", "UESC WRAITH WARDEN", "UESC CMDR",
    };
    /// <summary>Dev mode never touches the Windows startup entry and enables the video-player test source.</summary>
    public bool DevMode { get; set; }
    public bool ShowZoneOutlines { get; set; }
    public string[] GameProcessNames { get; set; } = { "Marathon" };

    /// <summary>Dev mode: treat a video player showing a recorded clip as the game, so clips can be scrubbed to test detection.</summary>
    public bool DevWatchVideoPlayer { get; set; } = true;
    public string[] DevVideoPlayerProcesses { get; set; } = { "Video.UI", "Microsoft.Media.Player", "vlc", "mpc-hc64", "PotPlayerMini64" };
    /// <summary>Aspect ratio of the recordings (1920x1200 = 1.6). Used to find the picture inside a letterboxed player window.</summary>
    public double DevVideoAspect { get; set; } = 1.6;

    public string RosterHotkey { get; set; } = "F9";
    public bool FirstRunDone { get; set; }
    public bool HideOverlayFromCapture { get; set; }
    /// <summary>Show a grey meter next to runners nobody has rated; off: no meter for them.</summary>
    public bool ShowUnknownMeters { get; set; } = true;
    /// <summary>Check for a new version each time Marathon starts.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Also install preview releases (GitHub pre-releases), not only stable ones.</summary>
    public bool PreviewUpdates { get; set; }
    public int BadgeLingerMs { get; set; } = 3500;
    public string[] DeathKeywords { get; set; } = { "NEURAL LINK SEVERED" };
    public string[] ExfilKeywords { get; set; } = { "EXFILTRATED", "EXFIL SUCCESSFUL", "EXTRACTION SUCCESSFUL", "RUN COMPLETE" };

    public ScoringRules Scoring { get; set; } = new();
    public List<Zone> Zones { get; set; } = DefaultZones();

    public Zone? GetZone(string name) => Zones.FirstOrDefault(z => z.Name == name);

    // Measured from 1920x1200 recordings (see README "Calibrating zones").
    public static List<Zone> DefaultZones() => new()
    {
        // Kill feed and pings ("X PINGED Y" / "X PINGED + RUNNER") stack upward above the squad list.
        new Zone { Name = FeedZone, X = 0.0, Y = 0.50, W = 0.42, H = 0.38, IntervalMs = 1500, Passes = new[] { "BrightText" } },
        // Proximity chat: "NAME#1234 (speaker icon)" in red, mid-left. Small text, so OCR at 3x on saturated and bright pixels.
        new Zone { Name = ProxZone, X = 0.0, Y = 0.22, W = 0.30, H = 0.28, IntervalMs = 400, Scale = 3, MinSightings = 2,
                   Passes = new[] { "ColorText", "BrightText" } },
        // Your own name under the health bar; doubles as the "in a match" signal. Wide enough for long names
        // (~26 characters after the squad badge), ending before the key hint further right.
        new Zone { Name = SelfZone, X = 0.02, Y = 0.94, W = 0.18, H = 0.04, IntervalMs = 2000, Scale = 3, MinSightings = 1 },
        // Death recap ("NEURAL LINK SEVERED"). Only used to end the run; the names there are who damaged you.
        new Zone { Name = DeathZone, X = 0.68, Y = 0.18, W = 0.32, H = 0.62, IntervalMs = 1500, MinSightings = 1, Passes = new[] { "BrightText" } },
        // Banner text for successful extraction.
        new Zone { Name = EndZone, X = 0.2, Y = 0.05, W = 0.6, H = 0.4, IntervalMs = 2000, MinSightings = 1, Passes = new[] { "BrightText" } },
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Json);
                if (s != null)
                {
                    if (s.SettingsVersion < CurrentVersion)
                    {
                        // Zone layout changed between versions; keep user preferences, refresh calibration.
                        s.Zones = DefaultZones();
                        s.SettingsVersion = CurrentVersion;
                        s.Save();
                    }
                    foreach (var dz in DefaultZones())
                        if (s.GetZone(dz.Name) == null) s.Zones.Add(dz);
                    // Bots added to the list in a later version reach settings saved by an earlier one.
                    foreach (var n in DefaultIgnoredNames)
                        if (!s.IgnoredNames.Contains(n, StringComparer.OrdinalIgnoreCase)) s.IgnoredNames.Add(n);
                    return s;
                }
            }
        }
        catch (Exception ex) { Log.Error("Failed to read settings, using defaults", ex); }
        var fresh = new AppSettings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.DataDir);
        File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Json));
    }
}
