using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SleazeDetector.Core;

namespace SleazeDetector.UI;

/// <summary>Settings, in the same frameless, always-on-top style as the runner list.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly CheckBox _startup, _updates, _preview, _hideCapture, _unknown, _zones;
    private readonly TextBox _roster;

    public event Action? Saved;
    /// <summary>"Quit" pressed: stop the app.</summary>
    public event Action? QuitRequested;

    public SettingsWindow(AppSettings settings, bool devMode, Reputation.ReputationService rep, bool firstRun = false)
    {
        _s = settings;
        Title = "Sleaze Detector Settings";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AppIcon.Apply(this);
        var res = Application.Current.Resources;

        var p = new StackPanel();

        // Header: title, Close in the top right. Dragging the header moves the window.
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14), Background = Brushes.Transparent };
        var close = new Button { Content = "Close", Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = firstRun ? "Welcome to Marathon Sleaze Detector" : "Settings", FontSize = 18, FontWeight = FontWeights.SemiBold });
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        p.Children.Add(head);
        if (firstRun)
        {
            p.Children.Add(new TextBlock
            {
                Text = "Marathon must run in Borderless or Windowed mode for the overlay to appear.",
                Style = (Style)res["Label"], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -6, 0, 12),
            });
            Closed += (_, _) => { if (!_s.FirstRunDone) { _s.FirstRunDone = true; _s.Save(); } };
        }

        _startup = new CheckBox { Content = "Automatically start", IsChecked = _s.StartWithWindows };
        p.Children.Add(_startup);
        if (devMode)
        {
            _startup.IsEnabled = false;
            p.Children.Add(Note("Dev mode: the startup entry is never changed."));
        }
        _updates = new CheckBox { Content = "Automatically check for updates", IsChecked = _s.AutoCheckUpdates };
        p.Children.Add(_updates);
        _preview = new CheckBox { Content = "Get preview versions", IsChecked = _s.PreviewUpdates };
        p.Children.Add(_preview);
        _unknown = new CheckBox { Content = "Show grey trust meter when unknown", IsChecked = _s.ShowUnknownMeters };
        p.Children.Add(_unknown);
        _hideCapture = new CheckBox { Content = "Hide overlay from screenshots and recordings", IsChecked = _s.HideOverlayFromCapture };
        p.Children.Add(_hideCapture);
        _zones = new CheckBox { Content = "Show zones (debug)", IsChecked = _s.ShowZoneOutlines };
        p.Children.Add(_zones);

        p.Children.Add(Field("Runner list hotkey", _roster = new TextBox { Text = _s.RosterHotkey, Width = 160, HorizontalAlignment = HorizontalAlignment.Left }));
        p.Children.Add(AccountSection(rep));

        // Bottom: Quit on the left, Save on the right; version and the data folder link on a line below.
        p.Children.Add(new Border { Height = 1, Background = (Brush)res["Line"], Margin = new Thickness(0, 20, 0, 14) });
        var actions = new DockPanel { LastChildFill = false };
        var quit = new Button { Content = "Quit Sleaze Detector" };
        quit.Click += (_, _) => { Close(); QuitRequested?.Invoke(); };
        DockPanel.SetDock(quit, Dock.Left);
        actions.Children.Add(quit);
        var save = new Button { Content = "Save", Style = (Style)res["PrimaryButton"], IsDefault = true, MinWidth = 96 };
        save.Click += (_, _) => Save();
        DockPanel.SetDock(save, Dock.Right);
        actions.Children.Add(save);
        p.Children.Add(actions);

        var meta = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        var version = new TextBlock { Text = $"Version {AppVersion.Display}", Style = (Style)res["Label"], FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(version, Dock.Left);
        meta.Children.Add(version);
        var folder = new TextBlock
        {
            Text = "Open data folder", Style = (Style)res["Label"], FontSize = 12, Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline, VerticalAlignment = VerticalAlignment.Center,
        };
        folder.MouseLeftButtonUp += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", Paths.DataDir) { UseShellExecute = true });
        DockPanel.SetDock(folder, Dock.Right);
        meta.Children.Add(folder);
        p.Children.Add(meta);

        Content = new Border
        {
            Background = (Brush)res["Bg"],
            BorderBrush = (Brush)res["Line"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Margin = new Thickness(12),
            Child = p,
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    /// <summary>Steam sign-in for sharing ratings. Everyone's ratings show without signing in.</summary>
    private UIElement AccountSection(Reputation.ReputationService rep)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        sp.Children.Add(new TextBlock { Text = "Shared ratings", FontWeight = FontWeights.SemiBold });
        var status = new TextBlock { Style = (Style)Application.Current.Resources["Label"], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        void Show()
        {
            var a = rep.Account;
            status.Text = a.SignedIn
                ? $"Signed in with Steam (account {a.SteamId}). Your ratings are shared with other players."
                : "Not signed in: you cannot rate runners until you sign in with Steam. Other players' ratings still show. "
                  + "Steam's sign-in page opens in your browser; only your Steam account number is sent to the ratings server.";
            button.Content = a.SignedIn ? "Sign out" : "Sign in with Steam";
            button.IsEnabled = true;
        }
        button.Click += async (_, _) =>
        {
            if (rep.Account.SignedIn) { rep.Account.SignOut(); Show(); return; }
            if (Reputation.ReputationService.Server is not { } server) return;
            button.IsEnabled = false;
            status.Text = "Waiting for the Steam sign-in in your browser...";
            bool ok = await rep.Account.SignInAsync(server);
            Show();
            if (!ok) status.Text = "Sign-in did not finish. " + status.Text;
        };
        Show();
        sp.Children.Add(status);
        sp.Children.Add(button);
        return sp;
    }

    private static TextBlock Note(string t) => new()
    {
        Text = t, Style = (Style)Application.Current.Resources["Label"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, -2, 0, 6),
    };

    private static StackPanel Field(string label, TextBox box)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["Label"], FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(box);
        return sp;
    }

    private void Save()
    {
        if (!HotkeyManager.TryParse(_roster.Text, out _, out _))
        {
            MessageBox.Show(this, $"Runner list hotkey '{_roster.Text}' isn't valid. Examples: F9, Ctrl+F9, Alt+Shift+R", "Settings");
            return;
        }
        _s.StartWithWindows = _startup.IsChecked == true;
        _s.AutoCheckUpdates = _updates.IsChecked == true;
        _s.PreviewUpdates = _preview.IsChecked == true;
        _s.ShowUnknownMeters = _unknown.IsChecked == true;
        _s.HideOverlayFromCapture = _hideCapture.IsChecked == true;
        _s.ShowZoneOutlines = _zones.IsChecked == true;
        _s.RosterHotkey = _roster.Text.Trim();
        _s.FirstRunDone = true;
        _s.Save();
        Saved?.Invoke();
        Close();
    }
}
