using System.Diagnostics;
using SleazeDetector.Native;

namespace SleazeDetector.Core;

public readonly record struct GameWindow(IntPtr Handle, int X, int Y, int Width, int Height, bool IsForeground);

public static class ScreenCheck
{
    /// <summary>
    /// True when the screen area really shows a window of one of <paramref name="pids"/>, sampled at five points.
    /// Stops the OCR from reading whatever is in front of the game (our review window, a browser, the desktop).
    /// <paramref name="passThrough"/> is our click-through overlay, which sits on top of the game by design.
    /// </summary>
    public static bool Shows(int x, int y, int w, int h, IReadOnlySet<uint> pids, IntPtr passThrough)
    {
        if (pids.Count == 0) return false;
        ReadOnlySpan<(double fx, double fy)> pts = [(0.5, 0.5), (0.15, 0.15), (0.85, 0.15), (0.15, 0.85), (0.85, 0.85)];
        foreach (var (fx, fy) in pts)
        {
            var root = Win32.RootAt(x + (int)(w * fx), y + (int)(h * fy));
            if (root == passThrough) continue;
            if (!pids.Contains(Win32.ProcessId(root))) return false;
        }
        return true;
    }

    /// <summary>
    /// True when at least one of five sample points of the area shows a window of <paramref name="pids"/>. Our own
    /// windows don't count, so the overlay hides when another app covers the game even if our review window is open.
    /// </summary>
    public static bool ShowsAny(int x, int y, int w, int h, IReadOnlySet<uint> pids)
    {
        if (pids.Count == 0) return false;
        ReadOnlySpan<(double fx, double fy)> pts = [(0.5, 0.5), (0.1, 0.1), (0.9, 0.1), (0.1, 0.9), (0.9, 0.9)];
        foreach (var (fx, fy) in pts)
            if (pids.Contains(Win32.ProcessId(Win32.RootAt(x + (int)(w * fx), y + (int)(h * fy))))) return true;
        return false;
    }
}

/// <summary>
/// Polls for the game process (cheap: one process-list lookup every couple of seconds while idle)
/// and tracks the game window's client rectangle in screen pixels.
/// </summary>
public sealed class GameWatcher
{
    private readonly Func<string[]> _processNames;
    private int _pid;
    private IntPtr _hwnd;

    public event Action? GameStarted;
    public event Action? GameExited;

    public bool IsRunning => _pid != 0;

    public IReadOnlySet<uint> OwnerPids => _pid == 0 ? new HashSet<uint>() : new HashSet<uint> { (uint)_pid };

    public GameWatcher(Func<string[]> processNames) => _processNames = processNames;

    /// <summary>Call periodically. Returns the current window state, or null if the game isn't running/visible.</summary>
    public GameWindow? Poll(Func<IntPtr, bool>? isOurWindow = null)
    {
        if (_pid != 0 && !Alive(_pid))
        {
            _pid = 0; _hwnd = IntPtr.Zero;
            Log.Info("Game exited");
            GameExited?.Invoke();
        }
        if (_pid == 0)
        {
            foreach (var name in _processNames())
            {
                var p = Process.GetProcessesByName(name).FirstOrDefault();
                if (p != null)
                {
                    _pid = p.Id;
                    Log.Info($"Game detected: {name} (pid {_pid})");
                    GameStarted?.Invoke();
                    break;
                }
            }
            if (_pid == 0) return null;
        }

        if (_hwnd == IntPtr.Zero || !Win32.IsWindowVisible(_hwnd)) _hwnd = FindMainWindow(_pid);
        if (_hwnd == IntPtr.Zero || Win32.IsIconic(_hwnd)) return null;

        Win32.GetClientRect(_hwnd, out var rc);
        var pt = new Win32.POINT();
        Win32.ClientToScreen(_hwnd, ref pt);
        if (rc.Width < 320 || rc.Height < 200) return null;

        var fg = Win32.GetForegroundWindow();
        // Our own window in front (review / roster) still counts, but only while the game is actually visible behind it.
        bool foreground = fg == _hwnd
            || ((isOurWindow?.Invoke(fg) ?? false) && ScreenCheck.ShowsAny(pt.X, pt.Y, rc.Width, rc.Height, OwnerPids));
        return new GameWindow(_hwnd, pt.X, pt.Y, rc.Width, rc.Height, foreground);
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static IntPtr FindMainWindow(int pid)
    {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out var wpid);
            if (wpid == pid && Win32.IsWindowVisible(h))
            {
                Win32.GetClientRect(h, out var r);
                long area = (long)r.Width * r.Height;
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
