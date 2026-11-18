namespace SleazeDetector.Core;

public static class Paths
{
    public static string DataDir { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarathonSleazeDetector");

    /// <summary>Dev tools only: keep everything (settings, ratings, sign-in, log) in another folder.</summary>
    public static void UseDataDir(string dir) => DataDir = dir;

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ReportsFile => Path.Combine(DataDir, "my-reports.json");
    public static string InstallIdFile => Path.Combine(DataDir, "install-id");
    public static string LogFile => Path.Combine(DataDir, DevToolLog ? "devtools-log.txt" : "log.txt");

    /// <summary>A dev tool is running (a "--..." test mode): it logs to its own file, not the app's log.</summary>
    public static bool DevToolLog { get; set; }
    public static string DebugDir => Path.Combine(DataDir, "debug");

    public static string InstallId
    {
        get
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(InstallIdFile)) return File.ReadAllText(InstallIdFile).Trim();
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(InstallIdFile, id);
            return id;
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
