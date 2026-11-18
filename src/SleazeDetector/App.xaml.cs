using System.Windows;
using System.Windows.Interop;
using SleazeDetector.Capture;
using SleazeDetector.Core;
using SleazeDetector.Detection;
using SleazeDetector.Native;
using SleazeDetector.Ocr;
using SleazeDetector.Reputation;
using SleazeDetector.Session;
using SleazeDetector.UI;
using WinForms = System.Windows.Forms;

namespace SleazeDetector;

public partial class App : Application
{
    private const string MutexName = @"Local\MarathonSleazeDetector";
    private const string TakeoverEventName = @"Local\MarathonSleazeDetector.Takeover";
    private const string SettingsEventName = @"Local\MarathonSleazeDetector.ShowSettings";

    private Mutex? _mutex;
    private EventWaitHandle? _takeover;
    private AppSettings _settings = null!;
    private bool _dev;
    private WinForms.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    // Created the first time the game (or, in dev mode, a clip player) shows up, then kept.
    private OverlayWindow _overlay = null!;
    private RunWindow _runWindow = null!;
    private ReputationService _rep = null!;
    private RunTracker _tracker = null!;
    private GameWatcher _watcher = null!;
    private VideoSourceFinder _video = null!;
    // Readers, OCR and the tray exist only while the game runs (see Activate / Deactivate).
    private Scanner? _scanner;
    private UpdateService _updates = null!;
    private ShellDetector? _shell;
    private DateTime _nextShellCheck = DateTime.MinValue;
    private FastHud? _fast;
    private bool _active, _windowsMade;
    private DateTime _goneSince = DateTime.MaxValue;
    // How long after the game is gone the readers are unloaded and the tray icon goes (a review window keeps it).
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(20);
    private Task? _ocrTask;
    private volatile bool _overlayVisible;
    private RunSession? _lastEnded;
    private IntPtr _overlayHwnd, _runHwnd;
    // The game's window as last seen by the loop (zero when it is not running).
    private IntPtr _gameHwnd;
    private readonly CancellationTokenSource _cts = new();

    private static readonly HashSet<string> DevTools =
    [
        "--ocr-test", "--meter-probe", "--ui-preview", "--capture-test", "--mem-test", "--harvest", "--read-test", "--fast-test",
        "--glyph-teach", "--learn-shell", "--shell-test", "--font-match", "--glyph-harvest", "--parse-test", "--rep-test",
    ];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // Dev tools (no tray, no overlay)
        Paths.DevToolLog = args.Length > 0 && DevTools.Contains(args[0]);
        if (args.Length > 0 && args[0] == "--ocr-test") { Shutdown(await Dev.OcrTest.RunAsync(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--meter-probe") { Shutdown(Dev.MeterProbe.Run(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--rep-test") { Shutdown(await Dev.RepTest.RunAsync(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--ui-preview") { Shutdown(Dev.UiPreview.Run(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--capture-test")
        {
            // Dev: grab the middle of the primary screen with Desktop Duplication and with GDI, save both.
            var b = WinForms.Screen.PrimaryScreen!.Bounds;
            var dd = Capture.DesktopDuplication.Shared.Capture(b.Width / 4, b.Height / 4, b.Width / 2, b.Height / 2);
            System.Threading.Thread.Sleep(200);
            dd = Capture.DesktopDuplication.Shared.Capture(b.Width / 4, b.Height / 4, b.Width / 2, b.Height / 2) ?? dd;
            var gdi = Frame.CaptureGdi(b.Width / 4, b.Height / 4, b.Width / 2, b.Height / 2);
            dd?.Save(args[1] + ".dd.png"); gdi.Save(args[1] + ".gdi.png");
            Log.Info($"capture-test: duplication={(dd != null ? "ok" : "unavailable")}");
            Shutdown(0); return;
        }
        if (args.Length > 0 && args[0] == "--mem-test") { Shutdown(Dev.MemTest.Run(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--harvest") { Shutdown(Dev.Harvest.Run(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--read-test") { Shutdown(Dev.ReadTest.Run(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--fast-test") { Shutdown(Dev.FastTest.Run(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--glyph-teach") { Shutdown(Dev.GlyphTeach.Run(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--learn-shell") { Shutdown(Dev.ShellLearn.Learn(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--shell-test") { Shutdown(Dev.ShellLearn.Test(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--font-match") { Shutdown(Dev.FontMatch.Run(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--glyph-harvest") { Shutdown(await Dev.GlyphHarvest.RunAsync(args.Skip(1).ToArray())); return; }
        if (args.Length > 0 && args[0] == "--parse-test") { Shutdown(await Dev.ParseTest.RunAsync(args.Skip(1).ToArray())); return; }

        DispatcherUnhandledException += (_, ex) => { Log.Error("Unhandled UI exception", ex.Exception); ex.Handled = true; };
        // So a stop is never silent in the log: crashes, unobserved task errors and every exit are written down.
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error($"Unhandled exception (terminating={ex.IsTerminating})", ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Info($"Exiting (code {Environment.ExitCode})");
        // Watchdog: if the scan loop stops going round for 10 s, say where it is stuck (once per stall).
        new Thread(() =>
        {
            bool reported = false;
            while (true)
            {
                Thread.Sleep(2000);
                var age = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _loopBeat), DateTimeKind.Utc);
                if (_loopBeat != 0 && age > TimeSpan.FromSeconds(10)) { if (!reported) Log.Info($"Scan loop stalled {age.TotalSeconds:0}s at: {_loopStage}"); reported = true; }
                else reported = false;
            }
        }) { IsBackground = true, Name = "watchdog" }.Start();
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        if (!AcquireSingleInstance(args.Contains(StartupManager.BackgroundArg))) { Shutdown(0); return; }

        _settings = AppSettings.Load();
#if DEBUG
        _dev = true;
#endif
        _dev |= args.Contains("--dev") || _settings.DevMode;
        Log.Info($"Starting v{AppVersion.Display} dev={_dev} exe={Environment.ProcessPath}");
        StartupManager.Sync(_settings.StartWithWindows, _dev);

        // Until the game starts only the game watcher runs: no readers, no OCR engine, no windows, no tray icon.
        _rep = new ReputationService(() => _settings);
        _updates = new UpdateService(() => _settings);
        _tracker = new RunTracker();
        _watcher = new GameWatcher(() => _settings.GameProcessNames);
        _video = new VideoSourceFinder(() => _settings);
        _tracker.RunEnded += _ => _fast?.Clear();
        // Your shell can't change during a run: evidence is collected per run and dropped when it ends.
        _tracker.RunEnded += _ => { _shell?.Reset(); _rep.PlayingRook = false; };

        _tracker.RunnerConfirmed += r => Dispatcher.BeginInvoke(() =>
        {
            _rep.Peek(r.Key);
            if (_windowsMade && _runWindow.IsVisible && _runWindow.Run == _tracker.Current) _runWindow.Refresh(_settings.RosterHotkey);
        });
        _tracker.RunEnded += run => Dispatcher.BeginInvoke(() => OnRunEnded(run));
        _updates.Downloaded += v => Dispatcher.BeginInvoke(() =>
            _tray?.ShowBalloonTip(4000, "Update ready", $"Version {v} installs the next time Marathon is closed.", WinForms.ToolTipIcon.None));
        // The killer from the death recap, read just after the run ended: show / refresh the review with them in it.
        _tracker.LateRunner += run => Dispatcher.BeginInvoke(() =>
        {
            if (_windowsMade && _runWindow.IsVisible && _runWindow.Run == run) _runWindow.Refresh();
            else OnRunEnded(run);
        });
        _tracker.RunStarted += run => Dispatcher.BeginInvoke(() => { if (_windowsMade) _overlay.ClearBadges(); });
        _watcher.GameExited += () => _tracker.EndSignal("game closed");

        if (!_settings.FirstRunDone) OpenSettings(firstRun: true);
        _ = _rep.SyncAsync();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    /// <summary>
    /// The game (or a clip player in dev mode) is up: load the readers and OCR, make the windows, show the tray icon and
    /// take the hotkeys. Runs on the UI thread.
    /// </summary>
    private void Activate()
    {
        if (_active) return;
        Log.Info("Game up: loading readers, tray icon on");
        if (!_windowsMade)
        {
            _overlay = new OverlayWindow(_rep, () => _settings);
            _overlay.Show(); _overlay.Hide(); // create the HWND now
            _overlayHwnd = _overlay.Handle;
            _runWindow = new RunWindow(_rep);
            _runWindow.SourceInitialized += (_, _) => _runHwnd = new WindowInteropHelper(_runWindow).Handle;
            _runWindow.GameHandle = () => _gameHwnd;
            _runWindow.EndRunRequested += () => _tracker.EndSignal("ended manually");
            _windowsMade = true;
        }
        try { _scanner = new Scanner(new HudOcr(), _tracker, () => _settings); }
        catch (Exception ex) { Log.Error("Windows OCR unavailable: squad list / own name / end screens are not read", ex); }
        var fast = new FastHud(GlyphReader.LoadEmbedded(), _tracker, () => _settings);
        // Names are read by the learned reader. The font matcher (the game's own HUD font) is loaded only if the model
        // cannot be.
        _ = Task.Run(() =>
        {
            if (NameNet.Load() is { } nn) { fast.Net = nn; Log.Info("Name model: ready"); }
            else if (FontReader.Load() is { } fr) fast.Font = fr;
        });
        _fast = fast;
        _shell = new ShellDetector(ShellDetector.LoadEmbedded());
        _ = GameAssets.LoadRookKillerIconAsync();
        SetupTray();
        RegisterHotkeys();
        _active = true;
        if (_settings.AutoCheckUpdates) _ = _updates.CheckAsync();
    }

    /// <summary>The game has been gone a while: drop the readers and OCR, the tray icon and the hotkeys. UI thread.</summary>
    private void Deactivate()
    {
        if (!_active) return;
        Log.Info("Game gone: readers unloaded, tray icon off");
        _active = false;
        _hotkeys?.UnregisterAll();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        if (_fast != null) { _fast.Net?.Dispose(); _fast.Net = null; _fast.Font = null; }
        _fast = null; _scanner = null; _shell = null; _ocrTask = null;
        GC.Collect();
    }

    /// <summary>
    /// One copy runs at a time. Starting the same exe again opens Settings in the running copy (with no tray icon while
    /// the game is closed, that is how you get to Settings and Quit). A different exe or another version (a freshly
    /// downloaded one) takes over instead: the running copy exits and the new one carries on.
    /// </summary>
    private bool AcquireSingleInstance(bool background)
    {
        _mutex = new Mutex(false, MutexName);
        _takeover = new EventWaitHandle(false, EventResetMode.AutoReset, TakeoverEventName);
        var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsEventName);
        string me = $"{Environment.ProcessPath}|{AppVersion.Display}";
        var instanceFile = Path.Combine(Paths.DataDir, "instance.txt");
        bool owned;
        try { owned = _mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            string running = "";
            try { running = File.ReadAllText(instanceFile).Trim(); } catch { }
            if (string.Equals(running, me, StringComparison.OrdinalIgnoreCase))
            {
                // The same app is already running: open its Settings (not for the Windows-startup launch).
                if (!background) showSettings.Set();
                Log.Info(background ? "Already running" : "Already running: opening Settings there");
                return false;
            }
            _takeover.Set();
            try { owned = _mutex.WaitOne(TimeSpan.FromSeconds(8)); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) { Log.Error("Another instance refused to exit"); return false; }
        }
        try { Directory.CreateDirectory(Paths.DataDir); File.WriteAllText(instanceFile, me); } catch { }
        var handle = _takeover;
        new Thread(() =>
        {
            handle.WaitOne();
            Log.Info("Another instance started; handing over");
            Dispatcher.BeginInvoke(() => Quit());
        }) { IsBackground = true, Name = "takeover-listener" }.Start();
        new Thread(() =>
        {
            while (true)
            {
                showSettings.WaitOne();
                Dispatcher.BeginInvoke(() => OpenSettings());
            }
        }) { IsBackground = true, Name = "settings-listener" }.Start();
        return true;
    }

    private bool? _lastSourceVideo;
    private long _loopBeat;
    private volatile string _loopStage = "start";

    private async Task Loop(CancellationToken ct)
    {
        DateTime nextZoneDraw = DateTime.MinValue, nextDiag = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _loopBeat, DateTime.UtcNow.Ticks);
            try
            {
                Func<IntPtr, bool> ours = h => h == _overlayHwnd || h == _runHwnd;
                _loopStage = "game watcher";
                var game = _watcher.Poll(ours);
                // Dev mode: with the game closed, a video player showing a clip stands in for the game.
                bool devVideo = _dev && _settings.DevWatchVideoPlayer && !_watcher.IsRunning;
                if (devVideo) game = _video.Poll(ours);
                // Updates: checked / downloaded in the background, applied only while the game is closed.
                _updates.Tick(_watcher.IsRunning);
                // Nothing to watch: stay idle (no readers loaded, no tray icon); wake up when the game shows up.
                bool present = _watcher.IsRunning || (devVideo && game != null);
                if (present) _goneSince = DateTime.MaxValue;
                else if (_goneSince == DateTime.MaxValue) _goneSince = DateTime.Now;
                if (!_active)
                {
                    if (!present)
                    {
                        _loopStage = "idle";
                        _tracker.Tick(gameVisible: false);
                        await Task.Delay(3000, ct);
                        continue;
                    }
                    await Dispatcher.InvokeAsync(Activate);
                }
                else if (!present && DateTime.Now - _goneSince > IdleAfter)
                {
                    // Keep everything while a review / runner list is open; unload once it is closed.
                    await Dispatcher.InvokeAsync(() => { if (!_runWindow.IsVisible) Deactivate(); });
                    if (!_active) continue;
                }
                var fast = _fast!;
                // Switching between the clip player and the game: a run seen in one never continues in the other.
                if (_lastSourceVideo is bool was && was != devVideo)
                {
                    _tracker.Abandon(devVideo ? "switched to the clip player" : "switched to the game");
                    fast.Clear();
                }
                _lastSourceVideo = devVideo;
                _gameHwnd = game?.Handle ?? IntPtr.Zero;
                _loopStage = "overlay track (UI thread)";
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay.Track(game);
                    _overlayVisible = _overlay.IsVisible;
                    if (DateTime.Now > nextZoneDraw)
                    {
                        _overlay.DrawZones(_settings.ShowZoneOutlines ? _settings.Zones : null);
                        nextZoneDraw = DateTime.Now.AddSeconds(1);
                    }
                });
                _loopStage = "diag";
                if (_dev && DateTime.Now > nextDiag)
                {
                    nextDiag = DateTime.Now.AddSeconds(3);
                    string cap = "";
                    if (game is { } gd)
                    {
                        var probe = Frame.CaptureScreen(gd.X + gd.Width / 4, gd.Y + gd.Height / 4, Math.Max(1, gd.Width / 2), Math.Max(1, gd.Height / 2));
                        long sum = 0; for (int i = 0; i < probe.Bgra.Length; i += 16) sum += probe.Bgra[i] + probe.Bgra[i + 1] + probe.Bgra[i + 2];
                        cap = $" captureMean={sum * 4.0 / probe.Bgra.Length / 3:0}";
                    }
                    // When the picture doesn't count as visible, say what window is in front of it.
                    string front = "";
                    if (game is { IsForeground: false } gf)
                    {
                        var hit = Win32.RootAt(gf.X + gf.Width / 2, gf.Y + gf.Height / 2);
                        front = $" front={Win32.ClassName(hit)}/pid{Win32.ProcessId(hit)} fg={Win32.ClassName(Win32.GetForegroundWindow())}/pid{Win32.ProcessId(Win32.GetForegroundWindow())}";
                    }
                    if (game is { } gz && gz.X <= 0 && gz.Y <= 0 && gz.Width >= WinForms.Screen.PrimaryScreen!.Bounds.Width)
                        Log.Info("diag z-order: " + Win32.ZOrder(6, _overlayHwnd));
                    Log.Info($"diag: source={(devVideo ? (_video.IsFullscreen ? "video(fullscreen)" : "video") : "game")} rect={(game is { } gg ? $"{gg.X},{gg.Y} {gg.Width}x{gg.Height}" : "none")} visible={game?.IsForeground}{cap} overlay={_overlayVisible} names={fast.Visible.Count} shell={_shell?.Current ?? "?"} hud=[{fast.LastDiag}] run={(_tracker.Current != null ? "on" : "off")}{front}");
                }
                if (game is { IsForeground: true } g)
                {
                    var pids = devVideo ? _video.OwnerPids : _watcher.OwnerPids;
                    // A fullscreen store player is invisible to "window at point" checks; it was already confirmed as the foreground app.
                    Func<int, int, int, int, bool>? visible = devVideo && _video.IsFullscreen ? null
                        : (x, y, w, h) => ScreenCheck.Shows(x, y, w, h, pids, _overlayHwnd);

                    // Every frame: kill feed + prox names from pixels, meters placed exactly where the lines are now.
                    var grab = _dev ? Dev.FrameRecorder.Wrap(g, Frame.CaptureScreen) : Frame.CaptureScreen;
                    _loopStage = "kill feed / prox";
                    var badges = fast.Tick(g, grab, visible);
                    _loopStage = "shell / scanner";
                    if (_dev) Dev.FrameRecorder.EndTick();

                    // Which shell you play (ability icons), twice a second until known for this run.
                    if (_shell is { IsReady: true, Current: null } shell && DateTime.Now >= _nextShellCheck)
                    {
                        _nextShellCheck = DateTime.Now.AddMilliseconds(500);
                        shell.Observe(g, Frame.CaptureScreen);
                        if (shell.Current != null) { Log.Info($"Shell: {shell.Current}"); _tracker.HudSeen(); }
                        _rep.PlayingRook = shell.Current == ShellDetector.Rook;
                    }
                    _ = Dispatcher.BeginInvoke(() => _overlay.SetBadges(badges));

                    // Slow OCR (squad list, own name, death / exfil screens) runs alongside without holding up the meters.
                    if (_scanner is { } scanner && (_ocrTask == null || _ocrTask.IsCompleted))
                        _ocrTask = Task.Run(() => scanner.TickAsync(g, Frame.CaptureScreen, zoneVisible: visible), ct);
                    await Task.Delay(30, ct);
                }
                else
                {
                    _loopStage = "game hidden";
                    _ = Dispatcher.BeginInvoke(() => _overlay.SetBadges(Array.Empty<Detection.BadgeRequest>()));
                    fast.Clear();
                    _tracker.Tick(gameVisible: false);
                    await Task.Delay(_watcher.IsRunning || devVideo ? 300 : 3000, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Scan loop error", ex);
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
        }
    }

    private void OnRunEnded(RunSession run)
    {
        _lastEnded = run;
        _overlay.ClearBadges();
        int n = run.Confirmed.Count();
        if (n == 0) { if (_runWindow.IsVisible && _runWindow.CurrentMode == RunWindow.Mode.Roster) _runWindow.Hide(); return; }
        // A run closed because the next match started (the earlier one was left without an end screen): no window
        // over the new match, only the tray message.
        if (run.EndReason == RunTracker.MatchOver && !_runWindow.IsVisible)
        {
            _tray?.ShowBalloonTip(4000, "Marathon Sleaze Detector", $"Run over. {_settings.RosterHotkey} to rate the runners you met.", WinForms.ToolTipIcon.None);
            return;
        }
        // Pop the review without stealing focus from the game; the recap screen has a cursor anyway.
        bool wasOpen = _runWindow.IsVisible;
        _runWindow.ShowFor(run, RunWindow.Mode.Review, activate: wasOpen);
        _tray?.ShowBalloonTip(4000, "Marathon Sleaze Detector", $"Would you like to rate any users? {_settings.RosterHotkey} to open the list.", WinForms.ToolTipIcon.None);
    }

    private void ToggleRoster()
    {
        if (!_windowsMade) return;
        if (_runWindow.IsVisible) { _runWindow.Hide(); return; }
        var run = _tracker.Current;
        if (run != null) _runWindow.ShowFor(run, RunWindow.Mode.Roster, activate: true, _settings.RosterHotkey);
        else if (_lastEnded != null) _runWindow.ShowFor(_lastEnded, RunWindow.Mode.Review, activate: true);
        else _runWindow.ShowFor(null, RunWindow.Mode.Roster, activate: true, _settings.RosterHotkey);
    }

    private void RegisterHotkeys()
    {
        _hotkeys ??= new HotkeyManager();
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        if (!_hotkeys.Register(_settings.RosterHotkey, ToggleRoster)) failed.Add(_settings.RosterHotkey);
        // Dev builds: F10 records the next ~900 frames of what the kill feed / prox path sees (Dev.FrameRecorder), so
        // a live moment can be replayed offline. A beep says it started.
        if (_dev && !_hotkeys.Register(Dev.FrameRecorder.Hotkey, Dev.FrameRecorder.Start)) failed.Add(Dev.FrameRecorder.Hotkey);
        if (failed.Count > 0)
            _tray?.ShowBalloonTip(5000, "Hotkey unavailable", $"{string.Join(", ", failed)} is used by another program. Change it in Settings.", WinForms.ToolTipIcon.Warning);
    }

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "Marathon Sleaze Detector",
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        // The tray shows the Rook-killer profile icon read from this computer's Marathon install (it is never shipped
        // with the app); the app's own icon until it loads, or if the game is not installed.
        GameAssets.LoadRookKillerIconAsync().ContinueWith(t => Dispatcher.Invoke(() =>
        {
            if (_tray != null && GameAssets.ToTrayIcon(t.Result) is { } icon) _tray.Icon = icon;
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
        menu.Items.Add("Runner list", null, (_, _) => Dispatcher.Invoke(ToggleRoster));
        menu.Items.Add("End run && review", null, (_, _) => _tracker.EndSignal("ended manually"));
        menu.Items.Add("Rate a runner by name...", null, (_, _) => Dispatcher.Invoke(OpenRateByName));
        menu.Items.Add("Settings...", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("Check for updates", null, async (_, _) =>
        {
            var result = await _updates.CheckAsync();
            _tray?.ShowBalloonTip(4000, "Marathon Sleaze Detector", result, WinForms.ToolTipIcon.None);
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Quit));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Dispatcher.Invoke(ToggleRoster); };
    }

    private void OpenSettings() => OpenSettings(false);

    private RateByNameWindow? _rateByName;

    private void OpenRateByName()
    {
        // Already open: bring it to the front instead of opening a second one.
        if (_rateByName is { IsLoaded: true } open) { open.Activate(); return; }
        var w = new RateByNameWindow(_rep);
        _rateByName = w;
        w.Closed += (_, _) => { if (_rateByName == w) _rateByName = null; };
        w.Show();
        w.Activate();
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings(bool firstRun)
    {
        // Already open: bring it to the front instead of opening a second one.
        if (_settingsWindow is { IsLoaded: true } open) { open.Activate(); return; }
        var w = new SettingsWindow(_settings, _dev, _rep, firstRun);
        _settingsWindow = w;
        w.Closed += (_, _) => { if (_settingsWindow == w) _settingsWindow = null; };
        w.Saved += () =>
        {
            StartupManager.Sync(_settings.StartWithWindows, _dev);
            if (_active) RegisterHotkeys();
            if (_windowsMade) _overlay.ApplyCaptureExclusion();
            _ = _rep.SyncAsync();
        };
        w.QuitRequested += Quit;
        w.Show();
        w.Activate();
    }

    private void Quit()
    {
        Log.Info("Quit (tray or settings)");
        _cts.Cancel();
        _hotkeys?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        try { _mutex?.ReleaseMutex(); } catch { }
        Shutdown(0);
    }
}
