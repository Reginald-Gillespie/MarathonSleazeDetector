using System.Diagnostics;
using SleazeDetector.Native;

namespace SleazeDetector.Core;

/// <summary>
/// Dev mode test source: finds a video player window (Movies &amp; TV, Media Player, VLC, ...) showing a recorded
/// clip and reports the rectangle of the actual picture inside it, so the whole pipeline (OCR, overlay
/// badges, run tracking, review) can be exercised by playing or scrubbing a clip.
///
/// Store apps (Movies &amp; TV, Media Player) are an ApplicationFrameHost frame with the app's CoreWindow inside it.
/// The picture is drawn by that CoreWindow, which also covers the frame's title bar strip, so its rectangle (not the
/// frame's client area) is what the video is letterboxed into. The frame and CoreWindow handles change when the
/// player switches between windowed and fullscreen, so the search is repeated every second.
/// </summary>
public sealed class VideoSourceFinder
{
    private readonly Func<AppSettings> _settings;
    private Source? _src;
    private DateTime _nextSearch = DateTime.MinValue;
    private string _lastLogged = "";

    /// <param name="Fullscreen">Fullscreen store app: its windows report cloaked/hidden and stale rectangles, so the
    /// picture is taken to be the whole monitor and "on screen" means the player is the foreground app.</param>
    private sealed record Source(IntPtr Frame, IntPtr Content, bool ContentIsChild, HashSet<uint> Pids, bool Fullscreen = false);

    public VideoSourceFinder(Func<AppSettings> settings) => _settings = settings;

    /// <summary>Processes whose windows count as "the video" for occlusion checks.</summary>
    public IReadOnlySet<uint> OwnerPids => _src?.Pids ?? (IReadOnlySet<uint>)new HashSet<uint>();

    /// <summary>The player is fullscreen (cloaked windows): per-zone occlusion checks can't see it, focus is used instead.</summary>
    public bool IsFullscreen => _src?.Fullscreen == true;

    public GameWindow? Poll(Func<IntPtr, bool>? isOurWindow = null)
    {
        if (DateTime.Now >= _nextSearch || _src == null)
        {
            _nextSearch = DateTime.Now.AddSeconds(1);
            _src = Find();
            var desc = _src == null ? "none" : $"{Win32.ClassName(_src.Frame)} / {Win32.ClassName(_src.Content)}";
            if (desc != _lastLogged)
            {
                Log.Info($"Dev video source: {desc}");
                if (_src == null) LogPlayerWindows();
                _lastLogged = desc;
            }
        }
        if (_src == null || (!_src.Fullscreen && Win32.IsIconic(_src.Frame))) return null;

        int ax, ay, w, h;
        if (_src.Fullscreen)
        {
            var b = System.Windows.Forms.Screen.FromHandle(_src.Content).Bounds;
            (ax, ay, w, h) = (b.X, b.Y, b.Width, b.Height);
        }
        else if (_src.ContentIsChild)
        {
            Win32.GetWindowRect(_src.Content, out var wr);
            (ax, ay, w, h) = (wr.Left, wr.Top, wr.Width, wr.Height);
        }
        else
        {
            Win32.GetClientRect(_src.Content, out var rc);
            var pt = new Win32.POINT();
            Win32.ClientToScreen(_src.Content, ref pt);
            (ax, ay, w, h) = (pt.X, pt.Y, rc.Width, rc.Height);
        }
        if (w < 320 || h < 200) return null;

        // The picture is letterboxed to the recording's aspect ratio inside the content area.
        double aspect = _settings().DevVideoAspect;
        int vw = w, vh = (int)(w / aspect);
        if (vh > h) { vh = h; vw = (int)(h * aspect); }
        int vx = ax + (w - vw) / 2, vy = ay + (h - vh) / 2;

        // "Foreground" for a video means the picture is actually on screen: the player (or one of our own windows)
        // is what's visible at its centre. Focus doesn't matter; a clip can keep playing while you type elsewhere.
        bool onScreen = _src.Fullscreen
            ? _src.Pids.Contains(Win32.ProcessId(Win32.GetForegroundWindow()))
            : ScreenCheck.ShowsAny(vx, vy, vw, vh, _src.Pids);
        return new GameWindow(_src.Frame, vx, vy, vw, vh, onScreen);
    }

    private Source? Find()
    {
        var names = _settings().DevVideoPlayerProcesses.Select(n => n.ToLowerInvariant()).ToHashSet();
        var procNames = new Dictionary<uint, string>();
        string ProcName(uint pid)
        {
            if (procNames.TryGetValue(pid, out var n)) return n;
            try { using var p = Process.GetProcessById((int)pid); n = p.ProcessName.ToLowerInvariant(); }
            catch { n = ""; }
            return procNames[pid] = n;
        }

        Source? best = null; long bestArea = 0;
        Win32.EnumWindows((h, _) =>
        {
            if (!Win32.IsWindowVisible(h) || Win32.IsCloaked(h)) return true;
            uint pid = Win32.ProcessId(h);
            Source? cand = null;
            if (Win32.ClassName(h) == "ApplicationFrameWindow")
            {
                // Store app frame: match by the process of the CoreWindow hosted inside it.
                IntPtr core = IntPtr.Zero; uint corePid = 0;
                Win32.EnumChildWindows(h, (c, _) =>
                {
                    uint cp = Win32.ProcessId(c);
                    if (cp != pid && Win32.ClassName(c) == "Windows.UI.Core.CoreWindow" && names.Contains(ProcName(cp)))
                    { core = c; corePid = cp; return false; }
                    return true;
                }, IntPtr.Zero);
                if (core != IntPtr.Zero) cand = new Source(h, core, true, new HashSet<uint> { pid, corePid });
            }
            else if (names.Contains(ProcName(pid)))
                cand = new Source(h, h, false, new HashSet<uint> { pid });

            if (cand != null)
            {
                Win32.GetWindowRect(cand.Content, out var r);
                long area = (long)r.Width * r.Height;
                if (area > bestArea) { bestArea = area; best = cand; }
            }
            return true;
        }, IntPtr.Zero);
        return best ?? FindFullscreen(names, ProcName) ?? FindOnScreen(names, ProcName);
    }

    /// <summary>
    /// Fullscreen Movies &amp; TV detaches its CoreWindow from the frame; the CoreWindow then reports itself cloaked and
    /// the frame hidden, and "window at point" skips both. What still holds is that the player is the foreground app.
    /// </summary>
    private static Source? FindFullscreen(HashSet<string> names, Func<uint, string> procName)
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        uint fgPid = Win32.ProcessId(fg);
        var playerWindows = new List<(IntPtr h, uint pid)>();
        Win32.EnumWindows((h, _) =>
        {
            uint pid = Win32.ProcessId(h);
            if (Win32.IsWindowVisible(h) && Win32.ClassName(h) == "Windows.UI.Core.CoreWindow" && names.Contains(procName(pid)))
                playerWindows.Add((h, pid));
            return true;
        }, IntPtr.Zero);
        bool fgIsPlayer = names.Contains(procName(fgPid))
                          || (Win32.ClassName(fg) == "ApplicationFrameWindow" && playerWindows.Count > 0);
        if (!fgIsPlayer) return null;
        var pids = playerWindows.Select(p => p.pid).Append(fgPid).ToHashSet();
        return new Source(fg, fg, false, pids, Fullscreen: true);
    }

    /// <summary>
    /// Fullscreen store apps can report their windows as hidden or cloaked while they are on screen, so the window
    /// list misses them. Ask instead which window is really under the middle of each monitor.
    /// </summary>
    private static Source? FindOnScreen(HashSet<string> names, Func<uint, string> procName)
    {
        foreach (var scr in System.Windows.Forms.Screen.AllScreens)
        {
            var b = scr.Bounds;
            var hit = Win32.WindowFromPoint(new Win32.POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 });
            if (hit == IntPtr.Zero) continue;
            var root = Win32.GetAncestor(hit, Win32.GA_ROOT);
            uint hp = Win32.ProcessId(hit), rp = Win32.ProcessId(root);
            if (names.Contains(procName(hp)) || names.Contains(procName(rp)))
            {
                // Prefer the store app's CoreWindow (the picture); otherwise the top-level player window.
                var content = Win32.ClassName(hit) == "Windows.UI.Core.CoreWindow" ? hit : root;
                return new Source(root, content, content != root, new HashSet<uint> { hp, rp });
            }
        }
        return null;
    }

    /// <summary>Diagnostics for when a running player isn't found: every window of the player processes.</summary>
    private void LogPlayerWindows()
    {
        var names = _settings().DevVideoPlayerProcesses.Select(n => n.ToLowerInvariant()).ToHashSet();
        var pids = Process.GetProcesses().Where(p => names.Contains(p.ProcessName.ToLowerInvariant())).Select(p => (uint)p.Id).ToHashSet();
        if (pids.Count == 0) return;
        var lines = new List<string>();
        Win32.EnumWindows((h, _) =>
        {
            uint pid = Win32.ProcessId(h);
            bool host = Win32.ClassName(h) == "ApplicationFrameWindow";
            if (pids.Contains(pid) || host)
            {
                Win32.GetWindowRect(h, out var r);
                lines.Add($"{(long)h:X} pid={pid} class={Win32.ClassName(h)} vis={Win32.IsWindowVisible(h)} cloaked={Win32.IsCloaked(h)} rect={r.Left},{r.Top},{r.Right},{r.Bottom}");
            }
            return true;
        }, IntPtr.Zero);
        Log.Info("Player windows:\n  " + string.Join("\n  ", lines));
    }
}
