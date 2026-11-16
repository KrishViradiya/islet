using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Islet.Core;
using Islet.Interop;
using Forms = System.Windows.Forms;
using MediaState = Islet.Core.MediaState;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Islet;

public partial class IslandWindow : Window
{
    private enum Mode { Compact, Peek, Expanded, Drop }

    private const double BaseWindowWidth = 780, BaseWindowHeight = 660;
    private const double ExpandedWidth = 600, ExpandedPadding = 24; // panel is 568 wide + 16 each side; 12 above, ~12 below
    private const double PeekWidth = 390, PeekHeight = 66;
    private const int HotkeyId = 0xB001, MicHotkeyId = 0xB002;
    private const double BubbleGap = 6;

    private readonly IslandViewModel _vm;
    private readonly IslandSettings _s;
    private readonly Spring _w = new(), _h = new(), _r = new();
    private readonly Spring _bubbleOut = new(), _bubbleSize = new(); // the split bubble: how far out, how big (0..1)
    private CompactKind _lastPrimary = CompactKind.Idle;
    private bool _welcomePinned;
    private readonly SpringAnimator _animator;
    private readonly DispatcherTimer _hoverIn, _peekTimer, _dragLeave, _watch;
    private readonly DateTime _startedAt = DateTime.Now;

    private readonly LiquidEngine _liquid = new();
    private readonly DispatcherTimer _drip;
    private readonly Random _rng = new();

    private Mode _mode = Mode.Compact;
    private IntPtr _hwnd;
    private double _baseW, _baseH, _baseR, _pull;
    private DateTime? _cursorLeftAt;
    private bool _hidden, _draggingOut, _pinnedOpen, _liquidRunning;
    private Point _dragStart;
    private int _watchTicks, _tabIndex;
    private TimeSpan _liquidLast;
    private double _level, _lastCursorX, _lastDx;
    private int _shakeCount;
    private DateTime _shakeWindow = DateTime.Now;
    private DateTime _rimFlashUntil;

    public IslandWindow(IslandViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _s = vm.Settings;
        DataContext = vm;

        _animator = new SpringAnimator(new[] { _w, _h, _r, _bubbleOut, _bubbleSize }, ApplyShape);
        _w.Snap(130);
        _h.Snap(CompactHeight);
        _r.Snap(CompactHeight / 2);
        ApplyMotion();

        _hoverIn = OneShot(_s.HoverDelayMs, () => { if (_mode is Mode.Compact or Mode.Peek) SetMode(Mode.Expanded); });
        _peekTimer = OneShot(3200, () => { if (_mode == Mode.Peek) SetMode(Mode.Compact); });
        _dragLeave = OneShot(350, () => { if (_mode == Mode.Drop) SetMode(Mode.Compact); });
        _watch = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        _watch.Tick += (_, _) => Watch();

        _drip = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _drip.Tick += (_, _) =>
        {
            if (_vm.Dripping && !_hidden && _mode == Mode.Compact) DropBead(Palette.Blue, 5, 0.3 + _rng.NextDouble() * 0.4);
        };

        vm.Celebrate += brush => { DropBead(brush, 8, 0.5); FlashRim(brush); };
        vm.Jolt += brush => { _liquid.Shake(55); KickLiquid(); FlashRim(brush); };
        vm.ApprovalPending += () => { if (!_hidden) SetMode(Mode.Expanded); UpdateRim(); };
        vm.AlarmRinging += () => { if (!_hidden) SetMode(Mode.Expanded); };
        vm.Flash += FlashRim;
        vm.Agents.Approvals.CollectionChanged += (_, _) => UpdateRim();
        PillMascot.Poked += vm.Poke;
        HeaderMascot.Poked += vm.Poke;

        vm.PeekRequested += p => ShowPeek(p, respectStartup: true);
        vm.CompactChanged += () => { if (_mode == Mode.Compact) SetMode(Mode.Compact); };
        vm.PropertyChanged += OnViewModelChanged;
        vm.Media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaState.IsPlaying)) { UpdateEqualizer(); UpdateGlow(); UpdateRim(); KickLiquid(); }
        };
        _s.PropertyChanged += OnSettingChanged;
        ThemeManager.Changed += UpdateGlow;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeOverlay(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            RegisterHotkey();
            _vm.Clipboard.Attach(_hwnd);
            ApplyScale();
        };
        Loaded += (_, _) =>
        {
            ApplyStyle();
            ApplyShape();
            _vm.Start();
            SetMode(Mode.Compact);
            UpdateGlow();
            UpdateEqualizer();
            UpdateRim();
            _watch.Start();
            _drip.Start();

            // First run: open up and say hello (stays open until "Got it").
            if (_vm.ShowWelcome)
            {
                var hello = OneShot(1500, () => { _welcomePinned = true; SetPinned(true); });
                hello.Start();
            }
        };
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(Place, DispatcherPriority.Background);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(Place);
    }

    private bool IsNotch => _s.Style == IslandStyle.Notch;
    private double Shoulder => IsNotch ? 12 : 0;
    private double CompactHeight => IsNotch ? 32 : 34;

    private DispatcherTimer OneShot(int ms, Action onTick)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        t.Tick += (_, _) => { t.Stop(); onTick(); };
        return t;
    }

    // ================================================================== settings

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandSettings.Theme): ThemeManager.Apply(_s.Theme); UpdateGlow(); break;
            case nameof(IslandSettings.Scale): ApplyScale(); break;
            case nameof(IslandSettings.Monitor): Place(); break;
            case nameof(IslandSettings.Style):
            case nameof(IslandSettings.TopOffset): ApplyStyle(); break;
            case nameof(IslandSettings.Motion): ApplyMotion(); break;
            case nameof(IslandSettings.CollapsedOpacity):
            case nameof(IslandSettings.ExpandedOpacity): ApplyOpacity(); break;
            case nameof(IslandSettings.HotkeyEnabled):
            case nameof(IslandSettings.MicHotkey): RegisterHotkey(); break;
            case nameof(IslandSettings.HoverDelayMs): _hoverIn.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _s.HoverDelayMs)); break;
            case nameof(IslandSettings.HideInFullscreen): if (!_s.HideInFullscreen) SetHidden(false); break;
            case nameof(IslandSettings.MusicGlow): UpdateGlow(); break;
            case nameof(IslandSettings.LiquidEdge):
            case nameof(IslandSettings.LiquidMusic): UpdateRim(); KickLiquid(); ApplyShape(); break;
        }
        SetMode(_mode); // sizes may depend on what's enabled
    }

    private void ApplyScale()
    {
        double k = _s.Scale;
        Island.LayoutTransform = new ScaleTransform(k, k);
        Width = BaseWindowWidth * k;
        Height = BaseWindowHeight * k;
        Place();
    }

    private void ApplyStyle()
    {
        Island.Margin = new Thickness(0, IsNotch ? 0 : _s.TopOffset, 0, 0);
        ApplyShape();
    }

    /// <summary>Spring presets. Damping ratio &lt; 1 overshoots a little ("bouncy"), = 1 settles without overshoot.</summary>
    private void ApplyMotion()
    {
        var (k, zeta) = _s.Motion switch
        {
            MotionStyle.Smooth => (210.0, 0.95),
            MotionStyle.Snappy => (480.0, 0.85),
            _ => (250.0, 0.68),
        };
        void Tune(Spring s, double stiffness) { s.Stiffness = stiffness; s.Damping = 2 * zeta * Math.Sqrt(stiffness); }
        Tune(_w, k);
        Tune(_h, k * 0.8); // height trails width slightly: reads as "liquid" rather than a rigid box
        Tune(_r, k);
    }

    /// <summary>Puts the island at the top-centre of the chosen monitor (physical pixels, any DPI).</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var screens = Forms.Screen.AllScreens;
        var bounds = screens[Math.Min(_s.Monitor, screens.Length - 1)].Bounds;
        double k = Native.ScaleAt(bounds.Left + bounds.Width / 2, bounds.Top + 1);
        int wpx = (int)Math.Round(Width * k), hpx = (int)Math.Round(Height * k);
        Native.MoveTo(_hwnd, bounds.Left + (bounds.Width - wpx) / 2, bounds.Top, wpx, hpx);
    }

    private void RegisterHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        Native.UnregisterHotKey(_hwnd, MicHotkeyId);
        const uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        if (_s.HotkeyEnabled) Native.RegisterHotKey(_hwnd, HotkeyId, mods, 0x49 /* I */);
        if (_s.MicHotkey && !Native.RegisterHotKey(_hwnd, MicHotkeyId, mods, 0x4D /* M */))
            Log.Info("Ctrl+Alt+M is taken by another app");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HotkeyId: SetPinned(!_pinnedOpen); handled = true; break;
            case MicHotkeyId: _vm.ToggleMic(); handled = true; break;
        }
        return IntPtr.Zero;
    }

    // ================================================================== shape & modes

    private void SetMode(Mode mode)
    {
        _mode = mode;
        if (mode != Mode.Compact) _pull = 0;
        (_baseW, _baseH, _baseR) = TargetShape(mode);
        Retarget();

        Transition(CompactLayer, mode == Mode.Compact);
        Transition(PeekLayer, mode == Mode.Peek);
        Transition(ExpandedLayer, mode is Mode.Expanded or Mode.Drop);

        bool drop = mode == Mode.Drop;
        ShelfDropRect.Stroke = drop ? ThemeManager.Accent : (Brush)FindResource("Track");
        ShelfCard.BorderBrush = drop ? ThemeManager.Accent : Brushes.Transparent;
        ShelfHint.Text = drop ? "Release to park it here" : "Drop files on the island";

        ApplyOpacity();
    }

    private (double w, double h, double r) TargetShape(Mode mode) => mode switch
    {
        Mode.Compact => (CompactWidth(), CompactHeight, CompactHeight / 2),
        Mode.Peek => (PeekWidth, PeekHeight, IsNotch ? 26 : PeekHeight / 2),
        _ => (ExpandedWidth,
              Math.Min(ExpandedPanel.ActualHeight + ExpandedPadding, BaseWindowHeight - (IsNotch ? 0 : _s.TopOffset) - 30),
              30),
    };

    private double CompactWidth()
    {
        double extra = _s.ShowPrivacy ? _vm.PrivacyUses.Count * 14 : 0;
        if (_vm.MascotInPill) extra += 38;
        if (_vm.ShowCompactAgent) return 320 + extra;
        if (_vm.ShowCompactCall) return 230 + extra;
        if (_vm.ShowCompactTimer) return 240 + extra;
        if (_vm.ShowCompactDownload) return 320 + extra;
        if (_vm.ShowCompactActivity) return 300 + extra;
        if (_vm.ShowCompactMedia) return 220 + extra;
        double idle = 110 + (_vm.ClockInPillVisible ? 40 : 0) + (_vm.HasWeather ? 56 : 0);
        return Math.Max(140, idle + extra);
    }

    /// <summary>Feeds the springs their new targets (plus the liquid "reach" toward the cursor).</summary>
    private void Retarget()
    {
        double pull = _mode == Mode.Compact ? _pull : 0;
        _w.Target = _baseW + 12 * pull;
        _h.Target = _baseH + 3 * pull;
        _r.Target = _mode == Mode.Compact ? _h.Target / 2 : _baseR;

        // The bubble splits off only in the folded state; opening the island swallows it back.
        bool bubble = _mode == Mode.Compact && _vm.HasBubble && !_hidden;
        _bubbleOut.Target = bubble ? 1 : 0;
        _bubbleSize.Target = bubble ? 1 : 0;
        _animator.Kick();

        // A new thing took over the pill: cross-fade its content in.
        if (_mode == Mode.Compact && _vm.Primary != _lastPrimary)
        {
            _lastPrimary = _vm.Primary;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            CompactLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        }
    }

    /// <summary>Runs every frame while springs move: rebuilds the island outline and the content clip.</summary>
    private void ApplyShape()
    {
        double w = Math.Max(_w.Value, 24), h = Math.Max(_h.Value, 12), s = Shoulder;
        double r = IsNotch ? Math.Clamp(_r.Value, 0, Math.Min(w / 2, Math.Max(0, h - s))) : Math.Clamp(_r.Value, 0, Math.Min(w, h) / 2);

        double left = IsNotch ? s : 0, right = left + w; // the body's x-range

        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            if (IsNotch)
            {
                // Flat top flush with the screen edge, concave "shoulders" melting into it, liquid bottom.
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(w + 2 * s, 0), false, false);
                c.ArcTo(new Point(right, s), new Size(s, s), 0, false, SweepDirection.Counterclockwise, false, false);
            }
            else
            {
                c.BeginFigure(new Point(r, 0), true, true);
                c.LineTo(new Point(w - r, 0), false, false);
                c.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            }
            c.LineTo(new Point(right, h - r), false, false);
            BottomEdge(c, left, right, h, r);
            if (IsNotch)
            {
                c.LineTo(new Point(s, s), false, false);
                c.ArcTo(new Point(0, 0), new Size(s, s), 0, false, SweepDirection.Counterclockwise, false, false);
            }
            else
            {
                c.LineTo(new Point(0, r), false, false);
                c.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            }
        }
        geometry.Freeze();
        Shape.Data = geometry;

        // The rim follows the same lower edge (corners included), as an open stroke.
        if (Rim.Opacity > 0.001 || Rim.HasAnimatedProperties)
        {
            var rim = new StreamGeometry();
            using (var c = rim.Open())
            {
                c.BeginFigure(new Point(right, h - r), false, false);
                BottomEdge(c, left, right, h, r);
            }
            rim.Freeze();
            Rim.Data = rim;
        }

        Island.Width = w + 2 * s;
        Island.Height = h;
        PlaceBubble(w, s);
        ClipHost.Margin = new Thickness(s, 0, 0, 0);
        ClipHost.Width = w;
        ClipHost.Height = h;
        ClipHost.Clip = IsNotch
            ? new RectangleGeometry(new Rect(0, -r, w, h + r), r, r) // top corners hidden above the screen edge
            : new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    /// <summary>
    /// The split bubble slides out from behind the island's right side and grows, like a droplet
    /// separating. Notch style: a small tab hanging from the edge; floating style: a circle.
    /// </summary>
    private void PlaceBubble(double w, double s)
    {
        double size = CompactHeight;
        double grow = Math.Clamp(_bubbleSize.Value, 0, 1.2);
        double k = _s.Scale;

        Bubble.LayoutTransform = Island.LayoutTransform;
        Bubble.Margin = Island.Margin;
        Bubble.Width = size * Math.Max(0.35, grow);
        Bubble.Height = size;
        BubbleShape.CornerRadius = IsNotch ? new CornerRadius(0, 0, size / 2, size / 2) : new CornerRadius(size / 2);
        BubbleContentScale.ScaleX = BubbleContentScale.ScaleY = Math.Clamp(grow, 0, 1);

        // Tucked behind the island at 0, fully separated at 1 (window-space pixels, so scale by k).
        double tucked = (w / 2) - size / 2;
        double separated = w / 2 + s + BubbleGap + size / 2;
        BubbleShift.X = k * (tucked + (separated - tucked) * Math.Clamp(_bubbleOut.Value, -0.1, 1.15));

        double opacity = Math.Clamp(grow * 1.4, 0, 1) * (_hidden ? 0 : _s.CollapsedOpacity);
        Bubble.Opacity = opacity;
        Bubble.IsHitTestVisible = opacity > 0.5;
    }

    /// <summary>
    /// Draws right corner → wobbly bottom → left corner. The membrane's points are joined with
    /// quadratic curves through their midpoints, so the liquid edge stays smooth at any amplitude.
    /// </summary>
    private void BottomEdge(StreamGeometryContext c, double left, double right, double h, double r)
    {
        c.ArcTo(new Point(right - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);

        double from = right - r, to = left + r, span = from - to;
        if (!_s.LiquidEdge || span < 8)
        {
            c.LineTo(new Point(to, h), true, true);
        }
        else
        {
            int n = LiquidEngine.Nodes;
            var pts = new Point[n];
            for (int k = 0; k < n; k++)
            {
                int i = n - 1 - k; // walk right → left; node 0 is the left end
                double u = i / (double)(n - 1);
                pts[k] = new Point(to + u * span, h + _liquid[i]);
            }
            for (int k = 1; k < n - 1; k++)
            {
                var mid = new Point((pts[k].X + pts[k + 1].X) / 2, (pts[k].Y + pts[k + 1].Y) / 2);
                c.QuadraticBezierTo(pts[k], mid, true, true);
            }
            c.LineTo(pts[n - 1], true, true);
        }

        c.ArcTo(new Point(left, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
    }

    // ================================================================== liquid: loop, rim, beads

    /// <summary>Starts the per-frame liquid simulation; it stops itself once the edge is calm.</summary>
    private void KickLiquid()
    {
        if (!_s.LiquidEdge || _liquidRunning) return;
        _liquidRunning = true;
        _liquidLast = TimeSpan.Zero;
        CompositionTarget.Rendering += OnLiquidFrame;
    }

    private void OnLiquidFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_liquidLast == TimeSpan.Zero) { _liquidLast = now; return; }
        if (now == _liquidLast) return;
        double dt = Math.Min((now - _liquidLast).TotalSeconds, 0.05);
        _liquidLast = now;

        // Music: follow the real speaker loudness (fast attack, slow release).
        bool music = _s.LiquidMusic && _s.ShowMedia && _vm.Media.IsPlaying && !_hidden;
        double target = music ? Math.Pow(AudioVolume.Peak(), 0.7) : 0;
        _level = target > _level ? _level + (target - _level) * 0.55 : _level * 0.9;
        _liquid.Music = _level;
        _liquid.SagTarget = _vm.IsAway && _s.SagWhenAway && _mode == Mode.Compact ? 1 : 0;

        _liquid.Step(dt);
        ApplyShape();

        if (!_s.LiquidEdge || (!music && _liquid.IsQuiet && _level < 0.005))
        {
            CompositionTarget.Rendering -= OnLiquidFrame;
            _liquidRunning = false;
        }
    }

    /// <summary>Base rim colour: amber pulse while an agent waits for you, accent while music plays.</summary>
    private void UpdateRim()
    {
        if (DateTime.Now < _rimFlashUntil) return;
        Rim.BeginAnimation(OpacityProperty, null);
        if (_vm.Agents.Approvals.Count > 0)
        {
            Rim.Stroke = Palette.Orange;
            Rim.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(700))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else if (_s.LiquidEdge && _s.LiquidMusic && _s.ShowMedia && _vm.Media.IsPlaying)
        {
            Rim.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
            Rim.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(500)));
        }
        else
        {
            Rim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
        }
        ApplyShape();
    }

    private void FlashRim(Brush brush)
    {
        _rimFlashUntil = DateTime.Now.AddMilliseconds(1400);
        Rim.Stroke = brush;
        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1400) };
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.08)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.6)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        flash.Completed += (_, _) => { _rimFlashUntil = DateTime.MinValue; UpdateRim(); };
        Rim.BeginAnimation(OpacityProperty, flash);
        ApplyShape();
    }

    /// <summary>The edge stretches, a drop pinches off and falls, and the surface snaps back.</summary>
    private void DropBead(Brush brush, double size, double at)
    {
        if (_hidden) return;
        _liquid.Kick(at, 70, 0.1);
        KickLiquid();

        double s = Shoulder, w = ClipHost.ActualWidth > 0 ? ClipHost.ActualWidth : _w.Value;
        double x = (IsNotch ? s : 0) + _baseR + at * Math.Max(0, w - 2 * _baseR) - size / 2;
        var bead = new System.Windows.Shapes.Ellipse { Width = size, Height = size * 1.15, Fill = brush, Opacity = 0 };
        var move = new TranslateTransform();
        bead.RenderTransform = move;
        Canvas.SetLeft(bead, x);
        Canvas.SetTop(bead, _h.Value - size * 0.5);
        BeadLayer.Children.Add(bead);

        var release = TimeSpan.FromMilliseconds(140);
        var fall = new DoubleAnimation(0, 46, TimeSpan.FromMilliseconds(850))
        {
            BeginTime = release,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(990) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.14)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.55)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        fade.Completed += (_, _) => BeadLayer.Children.Remove(bead);

        move.BeginAnimation(TranslateTransform.YProperty, fall);
        bead.BeginAnimation(OpacityProperty, fade);

        var snap = new DispatcherTimer { Interval = release };
        snap.Tick += (_, _) => { snap.Stop(); _liquid.Kick(at, -55, 0.1); KickLiquid(); };
        snap.Start();
    }

    private void Approval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Islet.Modules.ApprovalRequest request, Tag: string decision })
            _vm.Agents.Resolve(request, decision);
    }

    private static void Transition(FrameworkElement element, bool show)
    {
        if (element.IsHitTestVisible == show) return; // already heading there; don't restart mid-fade
        element.IsHitTestVisible = show;

        var duration = TimeSpan.FromMilliseconds(show ? 280 : 110);
        var begin = show ? TimeSpan.FromMilliseconds(110) : TimeSpan.Zero; // let the shape grow before content appears
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, duration) { BeginTime = begin, EasingFunction = ease });

        if (element.RenderTransform is ScaleTransform scale)
        {
            var anim = new DoubleAnimation(show ? 1 : 0.96, duration) { BeginTime = begin, EasingFunction = ease };
            if (show) anim.From = 0.93;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private void ApplyOpacity()
    {
        double target = _hidden ? 0 : _mode == Mode.Compact ? _s.CollapsedOpacity : _s.ExpandedOpacity;
        Island.IsHitTestVisible = !_hidden;
        Island.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>While music plays the island's shadow glows in the album colour.</summary>
    private void UpdateGlow()
    {
        bool glow = _s.MusicGlow && _s.ShowMedia && _vm.Media.IsPlaying && !ThemeManager.IsLight;
        var color = glow ? ThemeManager.AccentColor : Colors.Black;
        double opacity = glow ? 0.75 : ThemeManager.IsLight ? 0.2 : 0.5;
        var duration = TimeSpan.FromMilliseconds(700);
        Glow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(color, duration));
        Glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(opacity, duration));
        Glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(glow ? 36 : 26, duration));
    }

    private void ExpandedPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A card appeared/disappeared or the tab changed while open → spring to the new height.
        if (_mode is Mode.Expanded or Mode.Drop) SetMode(_mode);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.Tab)) SwitchTab();
        if (e.PropertyName == nameof(IslandViewModel.ShowWelcome) && !_vm.ShowWelcome && _welcomePinned)
        {
            _welcomePinned = false;
            SetPinned(false);
        }
    }

    /// <summary>Slide + fade between tabs, direction following the tab order.</summary>
    private void SwitchTab()
    {
        var panels = new FrameworkElement[] { HomePanel, LivePanel, ShelfPanel, ClipPanel }; // same order as IslandTab
        int index = (int)_vm.Tab;
        var target = panels[index];
        if (target.Visibility == Visibility.Visible) return;

        int direction = index >= _tabIndex ? 1 : -1;
        _tabIndex = index;
        foreach (var p in panels) p.Visibility = p == target ? Visibility.Visible : Visibility.Collapsed;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        if (target.RenderTransform is TranslateTransform slide)
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(26 * direction, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    // ================================================================== peek

    private void ShowPeek(PeekInfo peek, bool respectStartup)
    {
        if (_hidden || _mode is Mode.Expanded or Mode.Drop) return;
        if (respectStartup && DateTime.Now - _startedAt < TimeSpan.FromSeconds(2.5)) return; // don't flash what was there at launch

        PeekTitle.Text = peek.Title;
        PeekSubtitle.Text = peek.Subtitle;
        PeekGlyph.Text = peek.Glyph;
        PeekGlyph.Foreground = peek.Accent;
        PeekImage.Source = peek.Image;
        PeekImage.Visibility = peek.Image != null ? Visibility.Visible : Visibility.Collapsed;

        PeekBar.Visibility = peek.Progress.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (peek.Progress is double p)
            PeekBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(p, TimeSpan.FromMilliseconds(140)));

        if (_mode != Mode.Peek) SetMode(Mode.Peek);
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromMilliseconds(peek.Progress.HasValue ? 1500 : 3200);
        _peekTimer.Start();
    }

    // ================================================================== hover, liquid edge, pinning

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_hidden || _mode is not (Mode.Compact or Mode.Peek)) return;
        if (_s.HoverDelayMs <= 0) SetMode(Mode.Expanded);
        else _hoverIn.Start();
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinnedOpen);

    private void SetPinned(bool pinned)
    {
        _pinnedOpen = pinned;
        PinButton.Opacity = pinned ? 1 : 0.55;
        PinButton.Foreground = pinned ? ThemeManager.Accent : (Brush)FindResource("Fg");
        SetMode(pinned ? Mode.Expanded : Mode.Compact);
    }

    /// <summary>
    /// Runs 20×/second. Collapse is driven by the real cursor position (MouseLeave is unreliable for a
    /// window that never activates), and the same position drives the "liquid edge" reach.
    /// </summary>
    private void Watch()
    {
        bool over = CursorOverIsland(out var local);
        if (!over) _hoverIn.Stop();

        if (_mode == Mode.Compact && _s.LiquidEdge && !_hidden)
        {
            double reach = ReachToward(local);
            double pull = over ? 0.7 : reach;
            if (Math.Abs(pull - _pull) > 0.02) { _pull = pull; Retarget(); }

            // The membrane bulges right under the cursor as it approaches from below.
            double w = ClipHost.ActualWidth;
            _liquid.CursorAt = w > 0 ? Math.Clamp((local.X - Shoulder) / w, 0.06, 0.94) : 0.5;
            double bulge = over ? 0 : reach / 0.7;
            if (Math.Abs(bulge - _liquid.CursorPull) > 0.01) { _liquid.CursorPull = bulge; KickLiquid(); }
        }
        else if (_liquid.CursorPull > 0)
        {
            _liquid.CursorPull = 0;
            KickLiquid();
        }
        if (_vm.IsAway != _wasAway) { _wasAway = _vm.IsAway; KickLiquid(); } // start / stop sagging

        // Blip's eyes follow the cursor; he bobs to the music.
        if (Native.GetCursorPos(out var screen))
        {
            var cursor = new Point(screen.X, screen.Y);
            if (PillMascot.IsVisible) PillMascot.Tick(LookFrom(PillMascot, cursor), _level);
            if (HeaderMascot.IsVisible) HeaderMascot.Tick(LookFrom(HeaderMascot, cursor), _level);
        }
        DetectShake(over, local);

        bool busy = _draggingOut || _pinnedOpen || Mouse.Captured != null  // e.g. dragging the volume slider
                    || _vm.Agents.Approvals.Count > 0                      // waiting for Allow / Deny
                    || _vm.TimerHub.Ringing != null;                       // "time's up" until you answer
        if (_mode == Mode.Expanded && !busy)
        {
            if (over) _cursorLeftAt = null;
            else if (_cursorLeftAt is null) _cursorLeftAt = DateTime.Now;
            else if (DateTime.Now - _cursorLeftAt > TimeSpan.FromMilliseconds(350))
            {
                _cursorLeftAt = null;
                SetMode(Mode.Compact);
            }
        }

        if (++_watchTicks % 40 == 0) // every 2s
        {
            Native.KeepOnTop(_hwnd);
            SetHidden(_s.HideInFullscreen && Native.IsForegroundFullscreen(_hwnd));
        }
    }

    private bool _wasAway;

    private static Vector LookFrom(FrameworkElement eye, Point screenCursor)
    {
        try
        {
            var p = eye.PointFromScreen(screenCursor);
            double dx = p.X - eye.ActualWidth / 2, dy = p.Y - eye.ActualHeight / 2;
            return new Vector(Math.Clamp(dx / 260, -1, 1), Math.Clamp(dy / 140, -1, 1));
        }
        catch { return new Vector(); }
    }

    /// <summary>Wiggle the cursor back and forth over the island and Blip gets dizzy.</summary>
    private void DetectShake(bool over, Point local)
    {
        if (!over) { _shakeCount = 0; _lastDx = 0; _lastCursorX = local.X; return; }
        if (DateTime.Now - _shakeWindow > TimeSpan.FromSeconds(1.2)) { _shakeCount = 0; _shakeWindow = DateTime.Now; }

        double dx = local.X - _lastCursorX;
        _lastCursorX = local.X;
        if (Math.Abs(dx) < 5) return;
        if (_lastDx != 0 && Math.Sign(dx) != Math.Sign(_lastDx)) _shakeCount++;
        _lastDx = dx;

        if (_shakeCount >= 6)
        {
            _shakeCount = 0;
            _vm.MakeDizzy();
        }
    }

    /// <summary>0..1: how close the cursor is to the bottom of the collapsed island.</summary>
    private double ReachToward(Point local)
    {
        double w = ClipHost.ActualWidth, h = ClipHost.ActualHeight, s = Shoulder;
        if (local.Y < -4) return 0;
        double dx = Math.Abs(local.X - (s + w / 2)) - w / 2;
        double dy = local.Y - h;
        double vertical = Math.Clamp(1 - Math.Max(0, dy) / 70, 0, 1);
        double horizontal = Math.Clamp(1 - Math.Max(0, dx) / 70, 0, 1);
        return vertical * horizontal * 0.7;
    }

    private bool CursorOverIsland(out Point local)
    {
        local = default;
        if (!IsVisible || !Native.GetCursorPos(out var p)) return false;
        try { local = Island.PointFromScreen(new Point(p.X, p.Y)); }
        catch { return false; }
        double s = Shoulder;
        return local.X >= s && local.Y >= 0 && local.X <= s + ClipHost.ActualWidth && local.Y <= ClipHost.ActualHeight;
    }

    private void SetHidden(bool hide)
    {
        if (hide == _hidden) return;
        _hidden = hide;
        if (hide && _mode != Mode.Compact && !_pinnedOpen) SetMode(Mode.Compact);
        ApplyOpacity();
    }

    // ================================================================== media

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_s.WheelVolume || !_vm.VolumeKnown) return;
        _vm.ChangeVolume(e.Delta / 120.0 * 0.04);
        if (_mode is Mode.Compact or Mode.Peek)
        {
            var text = _vm.IsMuted ? "Muted" : $"{_vm.Volume * 100:0}%";
            ShowPeek(new PeekInfo("Volume", text, _vm.VolumeGlyph, null, ThemeManager.Accent, _vm.Volume), respectStartup: false);
        }
        e.Handled = true;
    }

    private void SeekBar_Click(object sender, MouseButtonEventArgs e)
    {
        if (SeekBar.ActualWidth <= 0) return;
        _vm.MediaModule.Seek(e.GetPosition(SeekBar).X / SeekBar.ActualWidth);
        e.Handled = true;
    }

    private void UpdateEqualizer()
    {
        bool playing = _vm.Media.IsPlaying;
        int i = 0;
        foreach (Rectangle bar in Equalizer.Children)
        {
            var scale = (ScaleTransform)bar.RenderTransform;
            if (playing)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(310 + i * 95))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    BeginTime = TimeSpan.FromMilliseconds(i * 70),
                });
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(250)));
            }
            i++;
        }
    }

    // ================================================================== timers & downloads

    private void LiveChip_Click(object sender, MouseButtonEventArgs e)
    {
        _vm.Tab = IslandTab.Live;
        e.Handled = true;
    }

    private void CustomTimer_Wheel(object sender, MouseWheelEventArgs e)
    {
        int step = _vm.CustomMinutes >= 60 ? 5 : 1;
        _vm.CustomMinutes += e.Delta > 0 ? step : -step;
        e.Handled = true; // don't change the volume
    }

    private void Download_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    /// <summary>Drag a finished download straight out of the island into a mail, chat or folder.</summary>
    private void Download_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { Tag: Islet.Modules.DownloadItem { IsDone: true } item }) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
        _draggingOut = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy | DragDropEffects.Link); }
        finally { _draggingOut = false; }
    }

    // ================================================================== clipboard

    private void Clip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ClipEntry entry }) return;
        _vm.Clipboard.Paste(entry);
        if (_s.PasteOnClick && !_pinnedOpen) SetMode(Mode.Compact);
    }

    // ================================================================== shelf: drop in

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (!AcceptsDrag(e)) return;
        _dragLeave.Stop();
        _vm.Tab = IslandTab.Shelf;
        if (_mode != Mode.Drop) SetMode(Mode.Drop);
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        if (AcceptsDrag(e)) _dragLeave.Stop();
    }

    private void Island_DragLeave(object sender, DragEventArgs e) => _dragLeave.Start();

    private void Island_Drop(object sender, DragEventArgs e)
    {
        _dragLeave.Stop();
        if (!_draggingOut && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _vm.ShelfModule.Add(paths);
        e.Handled = true;
        SetMode(Mode.Expanded); // shows the new item; Watch() collapses once the cursor leaves
    }

    private bool AcceptsDrag(DragEventArgs e)
    {
        bool ok = _s.ShowShelf && !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        return ok;
    }

    // ================================================================== shelf: drag out / open / remove

    private void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        if (e.ClickCount == 2 && sender is FrameworkElement { Tag: ShelfItem item })
        {
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("shelf open", ex); }
            e.Handled = true;
        }
    }

    private void ShelfItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { Tag: ShelfItem item }) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        _draggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            var result = DragDrop.DoDragDrop((DependencyObject)sender, data,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

            // If the target moved the file away, it no longer belongs on the shelf.
            if (result == DragDropEffects.Move && !File.Exists(item.Path) && !Directory.Exists(item.Path))
                _vm.ShelfModule.Remove(item);
        }
        finally { _draggingOut = false; }
    }

    private void ShelfItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelfItem item }) _vm.ShelfModule.Remove(item);
        e.Handled = true;
    }
}
