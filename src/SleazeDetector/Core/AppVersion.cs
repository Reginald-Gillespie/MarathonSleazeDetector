using System.Reflection;

namespace SleazeDetector.Core;

/// <summary>The app's version as people see it ("0.9.0-beta"), from the project's Version (build metadata dropped).</summary>
public static class AppVersion
{
    public static string Display { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "?").Split('+')[0];
}
