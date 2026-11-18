using Velopack;
using Velopack.Sources;

namespace SleazeDetector.Core;

/// <summary>
/// Updates from the project's GitHub releases (Velopack). A check runs each time Marathon starts (if automatic checks
/// are on) or from the tray menu; a new version downloads in the background and is installed the next time the game
/// is not running. Preview versions are GitHub pre-releases and are only considered with the preview setting on.
/// Copies not installed with the installer (dev builds, a loose exe) never update.
/// </summary>
public sealed class UpdateService
{
    /// <summary>The public GitHub repository releases are published to.</summary>
    public const string RepoUrl = "https://github.com/Reginald-Gillespie/MarathonSleazeDetector";

    private readonly Func<AppSettings> _settings;
    private UpdateManager? _mgr;
    private bool _mgrPreview;
    private UpdateInfo? _ready;
    private int _busy;

    /// <summary>A new version finished downloading (its version string).</summary>
    public event Action<string>? Downloaded;

    public UpdateService(Func<AppSettings> settings) { _settings = settings; }

    /// <summary>Installed with the installer, so updates can be applied.</summary>
    public bool Installed => Manager() != null;

    private UpdateManager? Manager()
    {
        bool preview = _settings().PreviewUpdates;
        if (_mgr != null && _mgrPreview == preview) return _mgr;
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: preview));
            _mgr = mgr.IsInstalled ? mgr : null;
            _mgrPreview = preview;
        }
        catch (Exception ex) { Log.Error("Updater unavailable", ex); _mgr = null; }
        return _mgr;
    }

    /// <summary>Called from the main loop: installs a downloaded update once the game is not running.</summary>
    public void Tick(bool gameRunning)
    {
        if (_ready == null || gameRunning || _mgr == null) return;
        Log.Info($"Applying update {_ready.TargetFullRelease.Version} and restarting");
        _mgr.ApplyUpdatesAndRestart(_ready);
    }

    /// <summary>Checks for a new version and downloads it. Returns a short result for the user.</summary>
    public async Task<string> CheckAsync()
    {
        var mgr = Manager();
        if (mgr == null) return "Updates only work in a copy installed with the installer.";
        if (_ready != null) return $"Version {_ready.TargetFullRelease.Version} is ready. It installs when Marathon is closed.";
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "Already checking for updates.";
        try
        {
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) return $"Up to date (version {AppVersion.Display}).";
            await mgr.DownloadUpdatesAsync(info);
            _ready = info;
            var v = info.TargetFullRelease.Version.ToString();
            Log.Info($"Update {v} downloaded");
            Downloaded?.Invoke(v);
            return $"Version {v} downloaded. It installs when Marathon is closed.";
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed ({ex.GetType().Name}: {ex.Message})");
            return "Could not check for updates.";
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
