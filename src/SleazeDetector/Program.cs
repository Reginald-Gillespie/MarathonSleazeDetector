using Velopack;

namespace SleazeDetector;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles the installer / updater hooks (install, update, uninstall) and exits when it is one.
        VelopackApp.Build().Run();
        // An identity of its own on the taskbar. Without it Windows matches the windows to the installer's Start menu
        // shortcut and shows the shortcut's icon instead of each window's own (the Rook icon from the game files).
        SetCurrentProcessExplicitAppUserModelID("MarathonSleazeDetector.App");
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
