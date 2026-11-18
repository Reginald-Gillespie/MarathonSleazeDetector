using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SleazeDetector.Core;
using SleazeDetector.Native;
using SleazeDetector.Reputation;

namespace SleazeDetector.UI;

/// <summary>
/// Transparent, click-through, always-on-top window over the part of the game where meters can appear (the kill feed
/// and prox chat areas, plus room for the meters to their right), or over the whole game while the zones are shown.
/// A transparent window is drawn in software at its full size, so it is kept as small as that.
/// Draws a TrustMeter next to each name where it appears on screen. Works with borderless/windowed
/// fullscreen (exclusive fullscreen bypasses the desktop compositor, so no overlay can draw there).
/// </summary>
public sealed class OverlayWindow : Window
{
    private sealed class Badge
    {
        public required TrustMeter Meter;
        public required string Key;
        public DateTime LastSeen;
        public double X, Y;          // position (DIPs, top-left)
        public bool Placed;
        public double Scale;
    }

    private readonly Canvas _canvas = new();
    private readonly Dictionary<string, Badge> _badges = new();
    private readonly List<UIElement> _zoneElems = new();
    private readonly ReputationService _rep;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private GameWindow _game;
    private Int32Rect _area;       // the window, in screen pixels
    private bool _areaFull;
    private double _dpi = 1.0;
    private DateTime _nextRaise;

    public OverlayWindow(ReputationService rep, Func<AppSettings> settings)
    {
        _rep = rep;
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "Sleaze Detector Overlay";
        Content = _canvas;
        Width = 10; Height = 10;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeClickThrough(_hwnd);
            ApplyCaptureExclusion();
        };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Prune(), Dispatcher);
        _timer.Start();
        _rep.Updated += key => Dispatcher.BeginInvoke(() =>
        {
            foreach (var b in _badges.Values.Where(b => b.Key == key)) { b.Meter.HideUnknown = !_settings().ShowUnknownMeters; b.Meter.Set(_rep.Peek(key)); }
        });
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Our badges must not be read back by our own OCR (or show up in the user's clips, unless they want them).</summary>
    public void ApplyCaptureExclusion()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.SetWindowDisplayAffinity(_hwnd, _settings().HideOverlayFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
    }

    public void Track(GameWindow? game)
    {
        if (game is not { } g || !g.IsForeground)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();
        bool full = _settings().ShowZoneOutlines;
        if (g != _game || full != _areaFull)
        {
            _game = g;
            _areaFull = full;
            _area = full ? new Int32Rect(g.X, g.Y, g.Width, g.Height) : MeterArea(g);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _area.X, _area.Y, _area.Width, _area.Height, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        // A fullscreen app (Movies & TV, a borderless game) that is activated after us lands above us even though we
        // are topmost: put the overlay back on top regularly. Never activates or moves it.
        if (DateTime.Now >= _nextRaise)
        {
            _nextRaise = DateTime.Now.AddMilliseconds(500);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
    }

    /// <summary>The kill feed and prox zones together, widened to the right by room for the meters.</summary>
    private Int32Rect MeterArea(GameWindow g)
    {
        var zones = _settings().Zones.Where(z => z.Name is AppSettings.FeedZone or AppSettings.ProxZone).ToList();
        if (zones.Count == 0) return new Int32Rect(g.X, g.Y, g.Width, g.Height);
        double x0 = zones.Min(z => z.X), y0 = zones.Min(z => z.Y);
        double x1 = Math.Min(1, zones.Max(z => z.X + z.W) + 0.1), y1 = Math.Min(1, zones.Max(z => z.Y + z.H) + 0.02);
        return new Int32Rect(g.X + (int)(x0 * g.Width), g.Y + (int)(y0 * g.Height),
                             (int)Math.Ceiling((x1 - x0) * g.Width), (int)Math.Ceiling((y1 - y0) * g.Height));
    }

    /// <summary>
    /// Sets the complete set of meters for this frame. Each meter sits just right of the end of its HUD line,
    /// vertically centred on the text and sized to it; meters not in the set are removed at once. Two meters on one
    /// line (both names are enemies) are placed side by side; a meter is never drawn over another.
    /// </summary>
    public void SetBadges(IReadOnlyList<Detection.BadgeRequest> requests)
    {
        var keep = new HashSet<string>();
        var placed = new List<Rect>();
        var perRunner = new Dictionary<string, int>();
        foreach (var r in requests.OrderBy(r => r.Y).ThenBy(r => r.X))
        {
            string baseId = $"{r.RunnerId}|{r.Slot.Split(':')[0]}";
            int n = perRunner[baseId] = perRunner.GetValueOrDefault(baseId) + 1;
            string id = $"{baseId}|{n}";
            double capDip = r.H / _dpi;
            double scale = Math.Clamp(capDip * 1.35 / 15.5, 0.55, 1.6);
            if (!_badges.TryGetValue(id, out var badge) || Math.Abs(badge.Scale - scale) > 0.15)
            {
                if (badge != null) _canvas.Children.Remove(badge.Meter);
                badge = new Badge { Meter = new TrustMeter(scale), Key = r.Key, Scale = scale };
                _badges[id] = badge;
                _canvas.Children.Add(badge.Meter);
            }
            badge.Key = r.Key;
            badge.Meter.HideUnknown = !_settings().ShowUnknownMeters;
            badge.Meter.Set(_rep.Peek(r.Key));
            badge.Meter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = badge.Meter.DesiredSize;

            double x = (r.X - _area.X) / _dpi + Math.Max(3, capDip * 0.6);
            double y = (r.Y - _area.Y) / _dpi + capDip / 2 - size.Height / 2;
            var rect = new Rect(x, y, size.Width, size.Height);
            // Same line: step right past the meter already there.
            foreach (var p in placed.Where(p => p.IntersectsWith(rect) && Math.Abs(p.Y - rect.Y) < size.Height / 2).ToList())
                rect.X = p.Right + 3;
            if (placed.Any(p => p.IntersectsWith(rect))) continue;   // would cover another line's meter
            placed.Add(rect);

            badge.X = rect.X; badge.Y = rect.Y; badge.Placed = true;
            badge.LastSeen = DateTime.Now;
            badge.Meter.Opacity = 1;
            Canvas.SetLeft(badge.Meter, rect.X);
            Canvas.SetTop(badge.Meter, rect.Y);
            keep.Add(id);
        }
        foreach (var (id, b) in _badges.Where(kv => !keep.Contains(kv.Key)).ToList())
        {
            _canvas.Children.Remove(b.Meter);
            _badges.Remove(id);
        }
    }

    private void Prune()
    {
        var linger = TimeSpan.FromMilliseconds(_settings().BadgeLingerMs);
        foreach (var (id, b) in _badges.ToList())
        {
            var age = DateTime.Now - b.LastSeen;
            if (age > linger + TimeSpan.FromMilliseconds(400))
            {
                _canvas.Children.Remove(b.Meter);
                _badges.Remove(id);
            }
            else if (age > linger) b.Meter.Opacity = Math.Max(0, 1 - (age - linger).TotalMilliseconds / 400);
        }
    }

    public void ClearBadges()
    {
        foreach (var b in _badges.Values) _canvas.Children.Remove(b.Meter);
        _badges.Clear();
    }

    /// <summary>Dev aid: outline the OCR zones so they can be calibrated against the HUD.</summary>
    public void DrawZones(IEnumerable<Zone>? zones)
    {
        foreach (var e in _zoneElems) _canvas.Children.Remove(e);
        _zoneElems.Clear();
        if (zones == null || _game.Width == 0) return;
        var stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xC6, 0xF4, 0x32));
        foreach (var z in zones.Where(z => z.Enabled))
        {
            double x = (_game.X + z.X * _game.Width - _area.X) / _dpi, y = (_game.Y + z.Y * _game.Height - _area.Y) / _dpi;
            var rect = new Rectangle
            {
                Width = z.W * _game.Width / _dpi, Height = z.H * _game.Height / _dpi,
                Stroke = stroke, StrokeDashArray = new DoubleCollection { 4, 3 }, StrokeThickness = 1,
            };
            var label = new TextBlock { Text = z.Name, FontSize = 10, Foreground = stroke, Margin = new Thickness(3, 1, 0, 0) };
            foreach (var e in new UIElement[] { rect, label })
            {
                Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
                _canvas.Children.Insert(0, e);
                _zoneElems.Add(e);
            }
        }
    }
}
