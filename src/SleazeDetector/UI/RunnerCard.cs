using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SleazeDetector.Detection;
using SleazeDetector.Reputation;
using SleazeDetector.Session;

namespace SleazeDetector.UI;

/// <summary>
/// One runner: who they were, what the tool saw, and a one-click Good / Bad experience (click again to un-rate).
/// Clicking Bad immediately files a generic bad report, then reveals optional detail checks
/// (backstab, toxic, ...) and a comment box; edits overwrite that same report.
/// </summary>
public sealed class RunnerCard : Border
{
    private readonly Runner _runner;
    private readonly RunSession _run;
    private readonly ReputationService _rep;
    private readonly TrustMeter _meter = new(1.1);
    private readonly TextBox _name;
    private readonly ToggleButton _good, _bad;
    private readonly StackPanel _details = new() { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly WrapPanel _checks = new();
    private readonly TextBox _comment;
    private readonly DispatcherTimer _debounce;
    private string? _verdict;
    private bool _loading;

    public string Key => _runner.Key;

    public RunnerCard(Runner runner, RunSession run, ReputationService rep)
    {
        _runner = runner; _run = run; _rep = rep;
        Background = (Brush)Application.Current.Resources["Panel"];
        BorderBrush = (Brush)Application.Current.Resources["Line"];
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(0);
        Padding = new Thickness(14, 12, 14, 12);
        Margin = new Thickness(0, 0, 0, 8);

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Save(); };

        var root = new StackPanel();

        // Header: relation chip | editable name | meter
        var header = new DockPanel { LastChildFill = true };
        var meterHost = new ContentControl { Content = _meter, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(meterHost, Dock.Right);
        header.Children.Add(meterHost);
        var chip = RelationChip(runner.Relation);
        DockPanel.SetDock(chip, Dock.Left);
        header.Children.Add(chip);
        _name = new TextBox
        {
            Text = runner.Name,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Brushes.Transparent, Padding = new Thickness(2, 0, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Misread? Click to fix the spelling before rating.",
        };
        _name.GotKeyboardFocus += (_, _) => _name.BorderBrush = (Brush)Application.Current.Resources["Accent"];
        _name.LostKeyboardFocus += (_, _) => { _name.BorderBrush = Brushes.Transparent; Rename(); };
        _name.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Keyboard_Clear(); };
        header.Children.Add(_name);
        root.Children.Add(header);

        // What we saw
        var context = new TextBlock
        {
            Text = ContextLine(runner),
            Style = (Style)Application.Current.Resources["Label"],
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10),
        };
        root.Children.Add(context);


        // Verdict buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _good = Toggle("Good", "Good");
        _bad = Toggle("Bad experience", "Bad");
        foreach (var b in new[] { _good, _bad }) buttons.Children.Add(b);
        // Clicking the chosen verdict again takes the rating back.
        _good.Click += (_, _) => Rate(Verdicts.Good);
        _bad.Click += (_, _) => Rate(Verdicts.Bad);
        root.Children.Add(buttons);

        // Details (revealed after choosing)
        _details.Children.Add(_checks);
        // Compact single line until clicked into; grows while typing and shrinks back when left empty.
        _comment = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 28, MaxLength = 400,
            Margin = new Thickness(0, 4, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _comment.GotKeyboardFocus += (_, _) => _comment.Height = 72;
        _comment.LostKeyboardFocus += (_, _) => { if (_comment.Text.Length == 0) _comment.Height = 28; };
        var hint = new TextBlock { Text = "Additional context", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 0) };
        _details.Children.Add(hint);
        _details.Children.Add(_comment);
        _comment.TextChanged += (_, _) => { if (!_loading) { _debounce.Stop(); _debounce.Start(); } };
        root.Children.Add(_details);

        Child = root;
        LoadExisting();
        // Rating needs a Steam sign-in: until then the details stay locked and the buttons open the sign-in.
        ApplySignIn();
        void OnAccount() => Dispatcher.BeginInvoke(ApplySignIn);
        Loaded += (_, _) => { _rep.Account.Changed -= OnAccount; _rep.Account.Changed += OnAccount; ApplySignIn(); };
        Unloaded += (_, _) => _rep.Account.Changed -= OnAccount;
        _meter.Set(_rep.Peek(Key));
    }

    private void ApplySignIn() => _details.IsEnabled = _rep.Account.SignedIn;

    /// <summary>
    /// A verdict button was clicked: rate (clicking the chosen verdict again takes the rating back). Not signed in:
    /// open the Steam sign-in instead, and rate once it succeeds.
    /// </summary>
    private async void Rate(string verdict)
    {
        if (_rep.Account.SignedIn) { SetVerdict(_verdict == verdict ? null : verdict); return; }
        UpdateVisuals();   // undo the toggle the click made
        if (ReputationService.Server is not { } server) return;
        if (await _rep.Account.SignInAsync(server)) SetVerdict(verdict);
    }

    private void Keyboard_Clear() => System.Windows.Input.Keyboard.ClearFocus();

    public void RefreshMeter() => _meter.Set(_rep.Peek(Key));

    private static string ContextLine(Runner r)
    {
        var parts = new List<string>();
        if (r.Sources.HasFlag(SightSource.Squad)) parts.Add("In your squad");
        if (r.Sources.HasFlag(SightSource.Prox)) parts.Add("Proximity chat");
        // Seen in a kill and on a ping: the kill is shown.
        if (r.Sources.HasFlag(SightSource.Ping) && !r.Sources.HasFlag(SightSource.KillFeed)) parts.Add("Pinged");
        if (r.Sources.HasFlag(SightSource.KillFeed)) parts.Add("Kill feed");
        if (r.Sources.HasFlag(SightSource.DeathScreen)) parts.Add("On your death recap");
        var notes = r.Notes.Where(n => !n.StartsWith("Pinged") && !n.StartsWith("Talked in proximity")).Take(4);
        var line = string.Join(", ", parts);
        var n = string.Join(", ", notes);
        return n.Length == 0 ? line : line.Length == 0 ? n : $"{line} | {n}";
    }

    private static Border RelationChip(Relation rel)
    {
        var (text, color) = rel switch
        {
            Relation.Teammate => ("SQUAD", Color.FromRgb(0x46, 0xA7, 0x58)),
            Relation.Enemy => ("ENEMY", Color.FromRgb(0xE5, 0x48, 0x4D)),
            _ => ("RUNNER", Color.FromRgb(0x8B, 0x92, 0xA3)),
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color) },
        };
    }

    private ToggleButton Toggle(string text, string automationName)
    {
        var t = new ToggleButton { Content = text, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.Resources["VerdictToggle"] };
        System.Windows.Automation.AutomationProperties.SetName(t, automationName);
        return t;
    }

    private void LoadExisting()
    {
        var existing = _rep.Find(Key);
        _loading = true;
        if (existing != null)
        {
            _verdict = existing.Verdict;
            _comment.Text = existing.Comment;
            if (_comment.Text.Length > 0) _comment.Height = 72;
            BuildChecks(existing.Flags);
        }
        _loading = false;
        UpdateVisuals();
    }

    private void SetVerdict(string? verdict)
    {
        bool changed = verdict != _verdict;
        _verdict = verdict;
        if (changed)
        {
            _loading = true;
            BuildChecks(new List<string>());
            _comment.Text = "";
            _loading = false;
        }
        UpdateVisuals();
        // The checks and comment box just opened: scroll the list so they are in view.
        if (verdict != null) Dispatcher.BeginInvoke(() => BringIntoView(), DispatcherPriority.Loaded);
        if (verdict == null) _rep.Retract(Key);
        else Save(); // file the generic report right away; details amend it
    }

    private void BuildChecks(List<string> selected)
    {
        _checks.Children.Clear();
        foreach (var c in RatingCategories.All.Where(c => c.Bad == (_verdict == Verdicts.Bad)))
        {
            if (c.TeammateOnly && _runner.Relation != Relation.Teammate && !selected.Contains(c.Id)) continue;
            var cb = new CheckBox { Content = c.Label, Tag = c.Id, IsChecked = selected.Contains(c.Id), ToolTip = c.Tip };
            cb.Click += (_, _) => Save();
            _checks.Children.Add(cb);
        }
    }

    private void UpdateVisuals()
    {
        var res = Application.Current.Resources;
        _good.IsChecked = _verdict == Verdicts.Good;
        _bad.IsChecked = _verdict == Verdicts.Bad;
        _good.Background = _verdict == Verdicts.Good ? Tint((SolidColorBrush)res["Good"]) : (Brush)res["PanelHi"];
        _bad.Background = _verdict == Verdicts.Bad ? Tint((SolidColorBrush)res["Bad"]) : (Brush)res["PanelHi"];
        _good.BorderBrush = _verdict == Verdicts.Good ? (Brush)res["Good"] : (Brush)res["Line"];
        _bad.BorderBrush = _verdict == Verdicts.Bad ? (Brush)res["Bad"] : (Brush)res["Line"];

        _details.Visibility = _verdict == null ? Visibility.Collapsed : Visibility.Visible;
        BorderBrush = _verdict switch { Verdicts.Bad => (Brush)res["Bad"], Verdicts.Good => (Brush)res["Good"], _ => (Brush)res["Line"] };
    }

    private static Brush Tint(SolidColorBrush b) => new SolidColorBrush(Color.FromArgb(0x40, b.Color.R, b.Color.G, b.Color.B));

    private void Save()
    {
        if (_verdict == null || _loading) return;
        var flags = _checks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        _rep.Submit(new Report
        {
            RunId = _run.RunId,
            TargetKey = Key,
            TargetName = _runner.Name,
            Verdict = _verdict,
            Flags = flags,
            Comment = _comment.Text.Trim(),
            Context = $"{_runner.Relation}; {ContextLine(_runner)}",
        });
    }

    private void Rename()
    {
        var newName = NameTools.Clean(_name.Text.Trim()) ?? _name.Text.Trim();
        if (newName.Length < 2 || newName == _runner.Name) { _name.Text = _runner.Name; return; }
        var oldKey = Key;
        bool hadReport = _rep.Find(oldKey) != null;
        _runner.NameOverride = newName;
        _name.Text = newName;
        if (hadReport && oldKey != Key && _rep.Account.SignedIn) { _rep.Retract(oldKey); Save(); }
        _meter.Set(_rep.Peek(Key));
    }
}
