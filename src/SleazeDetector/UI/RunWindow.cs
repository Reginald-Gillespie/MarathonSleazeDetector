using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SleazeDetector.Native;
using SleazeDetector.Reputation;
using SleazeDetector.Session;

namespace SleazeDetector.UI;

/// <summary>
/// One window, two modes:
///  - Roster (hotkey, mid-run): live list of runners detected so far, to rate someone mid-run.
///  - Review (auto, after the run): the same cards, offered for rating.
/// </summary>
public sealed class RunWindow : Window
{
    public enum Mode { Roster, Review }

    private readonly ReputationService _rep;
    private readonly StackPanel _list = new();
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    // "End run & review" while a run is live; closing is the header's Close button (or Esc) only.
    private readonly Button _primary = new();
    private readonly Dictionary<Guid, RunnerCard> _cards = new();
    private readonly Border _signInBar;
    private RunSession? _run;
    private readonly TextBlock _placeholder = new() { Text = "Nobody detected yet.", FontSize = 13, Margin = new Thickness(0, 8, 0, 8) };

    public Mode CurrentMode { get; private set; }
    public RunSession? Run => _run;
    public event Action? EndRunRequested;

    public RunWindow(ReputationService rep)
    {
        _rep = rep;
        Title = "Marathon Sleaze Detector";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        // Placement: see Place. A list that grows while open is only nudged up if it would pass the bottom margin. While
        // open the window never shrinks (a card folding back leaves room at the bottom instead of the window jumping);
        // past the height limit the list scrolls.
        MaxHeight = SystemParameters.WorkArea.Height * 0.8;
        SizeChanged += (_, e) =>
        {
            if (IsVisible && e.HeightChanged) PlaceSoon(center: false);
        };
        DpiChanged += (_, _) => { if (IsVisible) PlaceSoon(center: true); };
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AppIcon.Apply(this);

        _subtitle.Style = (Style)Application.Current.Resources["Label"];
        _placeholder.Style = (Style)Application.Current.Resources["Label"];
        _placeholder.FontSize = 13;
        _subtitle.FontSize = 12;
        _primary.Style = (Style)Application.Current.Resources["PrimaryButton"];

        var shell = new Border
        {
            Background = (Brush)Application.Current.Resources["Bg"],
            BorderBrush = (Brush)Application.Current.Resources["Line"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Margin = new Thickness(12),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var close = new Button { Content = "Close", Padding = new Thickness(10, 3, 10, 3), ToolTip = "Close (Esc)", VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, _) => Hide();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        head.Children.Add(titles);
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Grid.SetRow(head, 0);
        grid.Children.Add(head);

        // Shown above the list while not signed in: rating needs a Steam sign-in.
        var signInText = new TextBlock { Text = "Rating is off until you sign in with Steam.", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var signIn = new Button { Content = "Sign in with Steam", Margin = new Thickness(12, 0, 0, 0) };
        signIn.Click += async (_, _) =>
        {
            if (ReputationService.Server is not { } server) return;
            signIn.IsEnabled = false;
            signInText.Text = "Waiting for the Steam sign-in in your browser...";
            if (!await _rep.Account.SignInAsync(server)) signInText.Text = "Sign-in did not finish. Rating is off until you sign in with Steam.";
            signIn.IsEnabled = true;
        };
        var signInRow = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(signIn, Dock.Right);
        signInRow.Children.Add(signIn);
        signInRow.Children.Add(signInText);
        _signInBar = new Border
        {
            Background = (Brush)Application.Current.Resources["Panel"],
            BorderBrush = (Brush)Application.Current.Resources["Accent"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = signInRow,
        };
        void ShowSignIn() => _signInBar.Visibility = _rep.Account.SignedIn ? Visibility.Collapsed : Visibility.Visible;
        ShowSignIn();
        _rep.Account.Changed += () => Dispatcher.BeginInvoke(ShowSignIn);
        var body = new DockPanel();
        DockPanel.SetDock(_signInBar, Dock.Top);
        body.Children.Add(_signInBar);
        var scroll = new ScrollViewer { Content = _list, Padding = new Thickness(0, 0, 8, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // The wheel scrolls the list wherever the pointer is (text boxes on the cards would otherwise swallow it).
        scroll.PreviewMouseWheel += (_, e) =>
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta * 0.5);
            e.Handled = true;
        };
        body.Children.Add(scroll);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(_primary);
        Grid.SetRow(foot, 2);
        grid.Children.Add(foot);

        shell.Child = grid;
        Content = shell;

        _primary.Click += (_, _) => { if (CurrentMode == Mode.Roster && _run != null && _run.Ended == null) EndRunRequested?.Invoke(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        Closing += (_, e) => { e.Cancel = true; Hide(); }; // reused, never destroyed
        _rep.Updated += key => Dispatcher.BeginInvoke(() =>
        {
            foreach (var c in _cards.Values.Where(c => c.Key == key)) c.RefreshMeter();
        });
    }

    /// <summary>Returns the game's window handle (zero when the game is not running); the window opens on its monitor.</summary>
    public Func<IntPtr>? GameHandle { get; set; }

    // Work area of the monitor chosen when the window opened, in physical pixels, and that monitor's scale.
    private (Win32.RECT Work, double Scale)? _monitor;
    private bool _placeQueued, _placeCenter;

    /// <summary>
    /// The window opens centred on the game's monitor (or, without the game, the monitor the mouse pointer is on), at
    /// most 80% of that monitor's height so 10% stays clear above and below. The monitor is chosen once per opening.
    /// Placing waits until layout has finished, because moving the window from inside its own resize breaks its size.
    /// </summary>
    private void PlaceSoon(bool center)
    {
        _placeCenter |= center;
        if (_placeQueued) return;
        _placeQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _placeQueued = false;
            bool c = _placeCenter;
            _placeCenter = false;
            if (IsVisible) Place(c);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ChooseMonitor()
    {
        _monitor = Win32.MonitorOf(GameHandle?.Invoke() ?? IntPtr.Zero) ?? Win32.MonitorOf(IntPtr.Zero);
        if (_monitor is { } m) MaxHeight = m.Work.Height / m.Scale * 0.8;
    }

    private void Place(bool center)
    {
        // Monitor area in this window's units: physical pixels divided by the scale of the monitor it is on now.
        double own = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Rect area = _monitor is { } m
            ? new Rect(m.Work.Left / own, m.Work.Top / own, m.Work.Width / own, m.Work.Height / own)
            : SystemParameters.WorkArea;
        double margin = area.Height * 0.1;
        double left = Left, top = Top;
        if (center)
        {
            left = area.Left + (area.Width - ActualWidth) / 2;
            top = area.Top + (area.Height - ActualHeight) / 2;
        }
        else if (top + ActualHeight > area.Bottom - margin)
            top = Math.Max(area.Top + margin, area.Bottom - margin - ActualHeight);
        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top) > 0.5) Top = top;
        // Keep the height reached so far for as long as the window is open.
        if (ActualHeight > MinHeight + 0.5) MinHeight = Math.Min(ActualHeight, MaxHeight);
    }

    /// <summary>Populate without showing (also used by the dev screenshot tool).</summary>
    public void Prepare(RunSession? run, Mode mode, string? hotkeyHint = null)
    {
        CurrentMode = mode;
        if (run != _run) { _cards.Clear(); _list.Children.Clear(); }
        _run = run;
        Refresh(hotkeyHint);
    }

    public void ShowFor(RunSession? run, Mode mode, bool activate, string? hotkeyHint = null)
    {
        Prepare(run, mode, hotkeyHint);
        ShowActivated = activate;
        if (!IsVisible)
        {
            MinHeight = 0;
            ChooseMonitor();
            Show();
            PlaceSoon(center: true);
        }
        if (activate)
        {
            Activate();
            Win32.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }
    }

    public void Refresh(string? hotkeyHint = null)
    {
        var runners = _run?.Confirmed.OrderBy(r => r.Relation == Detection.Relation.Teammate ? 0 : 1).ThenBy(r => r.FirstSeen).ToList() ?? new();
        if (CurrentMode == Mode.Review)
        {
            _title.Text = runners.Count == 0 ? "No other runners were detected" : "Would you like to rate any users?";
            _subtitle.Text = "";
            _primary.Visibility = Visibility.Collapsed;
        }
        else
        {
            bool live = _run is { Ended: null };
            _title.Text = live ? $"Runners this run ({runners.Count})" : "No active run";
            _subtitle.Text = live
                ? (hotkeyHint != null ? $"{hotkeyHint} to open this window." : "")
                : "Runners show up here once a match starts.";
            _primary.Content = "End run & review";
            _primary.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_run == null) { _list.Children.Clear(); _cards.Clear(); return; }
        foreach (var r in runners)
        {
            if (_cards.ContainsKey(r.Id)) continue;
            var card = new RunnerCard(r, _run, _rep);
            _cards[r.Id] = card;
            _list.Children.Add(card);
        }
        // "Nobody detected yet" shows exactly while the list has no cards (the review title already says so).
        _list.Children.Remove(_placeholder);
        if (_cards.Count == 0 && CurrentMode == Mode.Roster) _list.Children.Add(_placeholder);
    }
}
