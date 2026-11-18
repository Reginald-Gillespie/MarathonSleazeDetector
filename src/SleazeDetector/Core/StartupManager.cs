using Microsoft.Win32;

namespace SleazeDetector.Core;

/// <summary>
/// Launch-at-login via HKCU\...\Run (per-user, no admin, the same mechanism Discord/Steam/Medal use).
/// The app then waits in the background and wakes up when the game process appears.
/// Every launch rewrites the entry with the current exe path, so moving or re-downloading the exe
/// replaces any stale entry that pointed somewhere else.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "MarathonSleazeDetector";
    public const string BackgroundArg = "--background";

    public static string? CurrentEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public static void Sync(bool enabled, bool devMode)
    {
        if (devMode)
        {
            Log.Info($"Dev mode: leaving startup entry untouched (current: {CurrentEntry() ?? "<none>"})");
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath!;
                var desired = $"\"{exe}\" {BackgroundArg}";
                var existing = key.GetValue(ValueName) as string;
                if (!string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, desired);
                    Log.Info(existing == null ? $"Registered startup: {desired}" : $"Replaced startup entry '{existing}' with '{desired}'");
                }
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Removed startup entry");
            }
        }
        catch (Exception ex) { Log.Error("Could not update startup entry", ex); }
    }
}
