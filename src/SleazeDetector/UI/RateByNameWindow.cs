using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SleazeDetector.Detection;
using SleazeDetector.Reputation;
using SleazeDetector.Session;

namespace SleazeDetector.UI;

/// <summary>
/// Rate a runner by typing their name, for someone the app did not pick up (or met outside a tracked run). The name
/// is turned into the same runner key the app uses for names it reads, so the rating lands on the same runner, and the
/// usual runner card (Good / Bad, checks, comment) is shown for it.
/// </summary>
public sealed class RateByNameWindow : Window
{
    // The server accepts letters and digits (look-alike letters folded) plus an optional four-digit tag.
    private static readonly Regex KeyShape = new(@"^[A-Z0-9*]{2,40}$");

    private readonly ReputationService _rep;
    private readonly TextBox _name = new() { FontSize = 15, Padding = new Thickness(4, 3, 4, 3) };
    private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ContentControl _cardHost = new() { Margin = new Thickness(0, 14, 0, 0) };
    // One session for everything rated from this window while it is open.
    private readonly RunSession _session = new() { EndReason = "rated by name" };

    public RateByNameWindow(ReputationService rep)
    {
        _rep = rep;
        Title = "Rate a runner by name";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AppIcon.Apply(this);
        var res = Application.Current.Resources;
        _error.Foreground = (Brush)res["Bad"];

        var p = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14), Background = Brushes.Transparent };
        var close = new Button { Content = "Close", Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = "Rate a runner by name", FontSize = 18, FontWeight = FontWeights.SemiBold });
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        p.Children.Add(head);

        p.Children.Add(new TextBlock
        {
            Text = "Type their name as the game shows it. The #1234 tag from proximity chat is optional: ratings go by " +
                   "the name alone, so NAME and NAME#1234 are the same runner.",
            Style = (Style)res["Label"], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        var row = new DockPanel();
        var find = new Button { Content = "Find", Style = (Style)res["PrimaryButton"], Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
        find.Click += (_, _) => Lookup();
        DockPanel.SetDock(find, Dock.Right);
        row.Children.Add(find);
        row.Children.Add(_name);
        p.Children.Add(row);
        p.Children.Add(_error);
        p.Children.Add(_cardHost);
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Lookup(); e.Handled = true; } };

        Content = new Border
        {
            Background = (Brush)res["Bg"],
            BorderBrush = (Brush)res["Line"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Margin = new Thickness(12),
            Child = p,
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Loaded += (_, _) => _name.Focus();
        _rep.Updated += OnUpdated;
        Closed += (_, _) => _rep.Updated -= OnUpdated;
    }

    private void OnUpdated(string key) => Dispatcher.BeginInvoke(() =>
    {
        if (_cardHost.Content is RunnerCard c && c.Key == key) c.RefreshMeter();
    });

    /// <summary>Checks a typed name. Returns what is wrong with it, or null; <paramref name="name"/> is the name tidied
    /// up (uppercase, no spaces around the #).</summary>
    public static string? Problem(string typed, out string name)
    {
        name = Regex.Replace(typed.Trim(), @"\s*#\s*", "#").ToUpperInvariant();
        if (name.Length == 0) return "Type a name first.";
        int hash = name.IndexOf('#');
        if (hash >= 0 && !Regex.IsMatch(name[(hash + 1)..], @"^[0-9]{4}$")) return "The tag after # is four digits, like NAME#1234.";
        if (hash >= 0 && hash < 3) return "The name before the tag needs at least three characters.";
        if (!KeyShape.IsMatch(NameTools.RunnerKey(name))) return "Names are letters and digits, 2 to 40 characters (plus an optional #1234 tag).";
        return null;
    }

    private void Lookup()
    {
        if (Problem(_name.Text, out var name) is { } problem)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            return;
        }
        _error.Visibility = Visibility.Collapsed;
        _name.Text = name;
        var runner = new Runner { FirstSeen = DateTime.Now, LastSeen = DateTime.Now, Confirmed = true, NameOverride = name };
        runner.Notes.Add("Rated by name");
        _session.Runners.Add(runner);
        _cardHost.Content = new RunnerCard(runner, _session, _rep);
    }
}
