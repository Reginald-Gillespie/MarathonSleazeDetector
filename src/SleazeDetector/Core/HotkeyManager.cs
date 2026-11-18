using System.Windows.Input;
using System.Windows.Interop;
using SleazeDetector.Native;

namespace SleazeDetector.Core;

/// <summary>System-wide hotkeys (RegisterHotKey) so they work while the game has focus.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("SleazeDetectorHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>Parses "F9", "Ctrl+Shift+R", "Alt+F10" ...; returns false (and logs) if invalid or taken by another app.</summary>
    public bool Register(string combo, Action action)
    {
        if (!TryParse(combo, out uint mods, out uint vk))
        {
            Log.Error($"Invalid hotkey '{combo}'");
            return false;
        }
        int id = _nextId++;
        if (!Win32.RegisterHotKey(_source.Handle, id, mods | Win32.MOD_NOREPEAT, vk))
        {
            Log.Error($"Hotkey '{combo}' is already in use by another program");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Win32.MOD_CONTROL; break;
                case "alt": mods |= Win32.MOD_ALT; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "win": mods |= Win32.MOD_WIN; break;
                default:
                    var keyText = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse<Key>(keyText, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a(); } catch (Exception ex) { Log.Error("Hotkey action failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
