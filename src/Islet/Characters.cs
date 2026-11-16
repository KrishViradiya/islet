using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Islet.Core;

namespace Islet;

/// <summary>Anything that can live in the island and react to what's happening.</summary>
public interface ICharacter
{
    MascotMood Mood { get; set; }
    /// <param name="look">Direction to the cursor, each axis -1..1.</param>
    /// <param name="beat">Music loudness 0..1.</param>
    void Tick(Vector look, double beat);
    event Action? Poked;
}

/// <summary>
/// Hosts the character the user picked in Settings and forwards moods / ticks to it.
/// Used in the folded pill, the island header and the Settings gallery.
/// </summary>
public sealed class CharacterHost : ContentControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(CharacterKind), typeof(CharacterHost),
        new PropertyMetadata(CharacterKind.Blip, (d, _) => ((CharacterHost)d).Recreate()));

    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(MascotMood), typeof(CharacterHost),
        new PropertyMetadata(MascotMood.Idle, (d, e) => { if (((CharacterHost)d)._current is { } c) c.Mood = (MascotMood)e.NewValue; }));

    private ICharacter? _current;

    public CharacterKind Kind { get => (CharacterKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public MascotMood Mood { get => (MascotMood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
    public event Action? Poked;

    public CharacterHost()
    {
        Width = 28;
        Height = 24;
        Focusable = false;
        Recreate();
    }

    public void Tick(Vector look, double beat) => _current?.Tick(look, beat);

    private void Recreate()
    {
        _current = Kind switch
        {
            CharacterKind.Mochi => new MochiView(),
            CharacterKind.Sprout => new SproutView(),
            CharacterKind.Orbit => new OrbitView(),
            _ => new MascotView(),
        };
        _current.Mood = Mood;
        _current.Poked += () => Poked?.Invoke();
        Content = _current;
    }

    public static string Describe(CharacterKind kind) => kind switch
    {
        CharacterKind.Mochi => "A soft dumpling. Squishes to the beat, blushes when things go well.",
        CharacterKind.Sprout => "A seedling in a pot. Leaves sway to music, droop when you're away, and it blooms on good news.",
        CharacterKind.Orbit => "A tiny planet whose moon speeds up while your agents work and parks while you sleep.",
        _ => "A little robot. Vibes to music, squints while agents work and gets dizzy if you poke him.",
    };
}

/// <summary>
/// Shared behaviour for the code-drawn characters: eyes that follow the cursor, blinking, mood faces
/// (open / happy ^^ / asleep - - / dizzy x x), a "?" when something needs you, floating "z" while
/// asleep, hops, squash-and-stretch and a squish when poked. Subclasses only draw a body.
/// </summary>
public abstract class CharacterBase : UserControl, ICharacter
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(MascotMood), typeof(CharacterBase),
        new PropertyMetadata(MascotMood.Idle, (d, _) => ((CharacterBase)d).OnMoodChanged()));

    protected readonly Canvas Stage = new() { Width = 28, Height = 24 };
    protected readonly ScaleTransform Squash = new();
    protected readonly RotateTransform Tilt = new();
    protected readonly TranslateTransform Hop = new();
    protected readonly Random Rng = new();
    protected double Time;

    private readonly TextBlock _ask, _zzz;
    private readonly TranslateTransform _askBob = new(), _zzzRise = new();
    private Canvas? _openEyes;
    private Shape? _happy, _sleep, _dizzy;
    private ScaleTransform? _lids;
    private TranslateTransform? _gaze;
    private double _gazeScale = 1, _gx, _gy, _spin;
    private DateTime _nextBlink = DateTime.Now.AddSeconds(2);

    public MascotMood Mood { get => (MascotMood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
    public event Action? Poked;

    protected CharacterBase()
    {
        Width = 28;
        Height = 24;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        ToolTip = "Poke me";

        Stage.RenderTransformOrigin = new Point(0.5, 1);
        Stage.RenderTransform = new TransformGroup { Children = { Squash, Tilt, Hop } };

        _zzz = Overlay("z", 10, "Dim", 25, -5, _zzzRise);
        _ask = Overlay("?", 12, "Accent", 26, -7, _askBob);
        Content = new Canvas { Width = 28, Height = 24, Children = { Stage, _zzz, _ask } };

        MouseLeftButtonDown += (_, e) => { Squish(); Poked?.Invoke(); e.Handled = true; };
    }

    // ------------------------------------------------------------------ drawing helpers

    protected static Ellipse Dot(double x, double y, double w, double h, Brush fill)
    {
        var e = new Ellipse { Width = w, Height = h, Fill = fill };
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    protected static Path Shape(string data, Brush? fill, Brush? stroke = null, double thickness = 0)
    {
        var p = new Path { Data = Geometry.Parse(data), Fill = fill, Stroke = stroke, StrokeThickness = thickness };
        p.StrokeStartLineCap = p.StrokeEndLineCap = PenLineCap.Round;
        p.StrokeLineJoin = PenLineJoin.Round;
        return p;
    }

    protected static Brush Solid(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    /// <summary>Adds the standard mood faces centred on (lx, y) and (rx, y).</summary>
    protected void AddFace(Panel into, double lx, double rx, double y, double eyeW, double eyeH, Brush eyeFill, Brush line,
                           double lineThickness = 1.5, double gazeScale = 1)
    {
        _gazeScale = gazeScale;
        _lids = new ScaleTransform { CenterX = (lx + rx) / 2, CenterY = y };
        _gaze = new TranslateTransform();
        _openEyes = new Canvas { RenderTransform = new TransformGroup { Children = { _lids, _gaze } } };
        foreach (var x in new[] { lx, rx })
        {
            var eye = new Rectangle { Width = eyeW, Height = eyeH, RadiusX = eyeW / 2, RadiusY = eyeW / 2, Fill = eyeFill };
            Canvas.SetLeft(eye, x - eyeW / 2);
            Canvas.SetTop(eye, y - eyeH / 2);
            _openEyes.Children.Add(eye);
        }
        double s = Math.Max(eyeW, 3) * 0.8;
        string F(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _happy = Shape($"M {F(lx - s)},{F(y + s * 0.6)} L {F(lx)},{F(y - s * 0.6)} L {F(lx + s)},{F(y + s * 0.6)} " +
                       $"M {F(rx - s)},{F(y + s * 0.6)} L {F(rx)},{F(y - s * 0.6)} L {F(rx + s)},{F(y + s * 0.6)}", null, line, lineThickness);
        _sleep = Shape($"M {F(lx - s)},{F(y)} L {F(lx + s)},{F(y)} M {F(rx - s)},{F(y)} L {F(rx + s)},{F(y)}", null, line, lineThickness);
        _dizzy = Shape($"M {F(lx - s * .8)},{F(y - s * .8)} L {F(lx + s * .8)},{F(y + s * .8)} M {F(lx + s * .8)},{F(y - s * .8)} L {F(lx - s * .8)},{F(y + s * .8)} " +
                       $"M {F(rx - s * .8)},{F(y - s * .8)} L {F(rx + s * .8)},{F(y + s * .8)} M {F(rx + s * .8)},{F(y - s * .8)} L {F(rx - s * .8)},{F(y + s * .8)}",
                       null, line, lineThickness);
        into.Children.Add(_openEyes);
        into.Children.Add(_happy);
        into.Children.Add(_sleep);
        into.Children.Add(_dizzy);
        OnMoodChanged();
    }

    private TextBlock Overlay(string text, double size, string brushKey, double x, double y, TranslateTransform move)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.Bold, Opacity = 0, RenderTransform = move, IsHitTestVisible = false };
        t.SetResourceReference(ForegroundProperty, brushKey);
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        return t;
    }

    // ------------------------------------------------------------------ behaviour

    /// <summary>Character-specific reactions on top of the shared ones.</summary>
    protected abstract void ApplyMood(MascotMood mood);

    /// <summary>Called ~20×/s; Time advances 0.05 per call.</summary>
    protected abstract void OnTick(double beat);

    public void Tick(Vector look, double beat)
    {
        if (!IsVisible) return;
        Time += 0.05;
        var mood = Mood;

        var (tx, ty) = mood switch
        {
            MascotMood.Focused => (look.X * 0.8, 1.0),
            MascotMood.Curious => (1.2, -0.9),
            MascotMood.Sleepy or MascotMood.Happy => (0.0, 0.0),
            _ => (look.X * 1.6, look.Y * 0.9),
        };
        if (mood == MascotMood.Dizzy)
        {
            _spin += 0.55;
            tx = Math.Cos(_spin) * 1.5;
            ty = Math.Sin(_spin) * 0.9;
            Tilt.Angle = Math.Sin(_spin * 0.45) * 12;
        }
        _gx += (tx - _gx) * 0.35;
        _gy += (ty - _gy) * 0.35;
        if (_gaze != null) { _gaze.X = _gx * _gazeScale; _gaze.Y = _gy * _gazeScale; }

        if (mood == MascotMood.Vibing)
        {
            Hop.Y = -beat * 3;
            Squash.ScaleY = 1 - beat * 0.08;
            Squash.ScaleX = 1 + beat * 0.06;
        }

        if (DateTime.Now >= _nextBlink && _openEyes?.Visibility == Visibility.Visible && mood != MascotMood.Focused)
        {
            _lids?.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(_lids.ScaleY, 0.1, TimeSpan.FromMilliseconds(70)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
            _nextBlink = DateTime.Now.AddSeconds(2.2 + Rng.NextDouble() * 4);
        }

        OnTick(beat);
    }

    private void OnMoodChanged()
    {
        var mood = Mood;
        bool open = mood is not (MascotMood.Happy or MascotMood.Sleepy or MascotMood.Dizzy);
        if (_openEyes != null) _openEyes.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (_happy != null) _happy.Visibility = mood == MascotMood.Happy ? Visibility.Visible : Visibility.Collapsed;
        if (_sleep != null) _sleep.Visibility = mood == MascotMood.Sleepy ? Visibility.Visible : Visibility.Collapsed;
        if (_dizzy != null) _dizzy.Visibility = mood == MascotMood.Dizzy ? Visibility.Visible : Visibility.Collapsed;

        Hop.BeginAnimation(TranslateTransform.YProperty, null);
        Squash.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Squash.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Hop.Y = 0;
        Squash.ScaleX = Squash.ScaleY = 1;
        if (_lids != null)
        {
            _lids.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _lids.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _lids.ScaleX = 1;
            _lids.ScaleY = mood == MascotMood.Focused ? 0.55 : 1;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (mood != MascotMood.Dizzy)
            Tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(mood == MascotMood.Curious ? 10 : 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });

        _ask.BeginAnimation(OpacityProperty, new DoubleAnimation(mood == MascotMood.Curious ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        _askBob.BeginAnimation(TranslateTransform.YProperty, mood == MascotMood.Curious
            ? new DoubleAnimation(0, -2.5, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);

        if (mood == MascotMood.Sleepy)
        {
            _zzzRise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(2, -9, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever });
            var fade = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.3)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            _zzz.BeginAnimation(OpacityProperty, fade);
            Squash.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.93, TimeSpan.FromSeconds(1.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        }
        else
        {
            _zzzRise.BeginAnimation(TranslateTransform.YProperty, null);
            _zzz.BeginAnimation(OpacityProperty, null);
            _zzz.Opacity = 0;
        }

        if (mood == MascotMood.Happy)
        {
            var hop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromPercent(0.22), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.45), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(-3.5, KeyTime.FromPercent(0.65), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.85), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            Hop.BeginAnimation(TranslateTransform.YProperty, hop);
        }

        if (mood == MascotMood.Alarmed && _lids != null)
        {
            var wide = new DoubleAnimation(1, 1.3, TimeSpan.FromMilliseconds(160)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
            _lids.BeginAnimation(ScaleTransform.ScaleXProperty, wide);
            _lids.BeginAnimation(ScaleTransform.ScaleYProperty, wide);
        }

        ApplyMood(mood);
    }

    private void Squish()
    {
        if (Mood is MascotMood.Sleepy or MascotMood.Vibing) return;
        Squash.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.78, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
        Squash.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.14, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
    }
}

/// <summary>Mochi: a soft cream dumpling with rosy cheeks.</summary>
public sealed class MochiView : CharacterBase
{
    private readonly Ellipse _cheekL, _cheekR;

    public MochiView()
    {
        var cream = Solid("#F4ECE0");
        var ink = Solid("#2A2522");
        Stage.Children.Add(Shape("M 2,22 C 1,14 6,6 14,6 C 22,6 27,14 26,22 C 22,24 6,24 2,22 Z", cream));
        Stage.Children.Add(Dot(8, 8.5, 5, 2.6, Solid("#99FFFFFF"))); // a little shine on top
        _cheekL = Dot(4.5, 15.5, 4.5, 2.6, Solid("#FF9AA8"));
        _cheekR = Dot(19, 15.5, 4.5, 2.6, Solid("#FF9AA8"));
        _cheekL.Opacity = _cheekR.Opacity = 0.55;
        Stage.Children.Add(_cheekL);
        Stage.Children.Add(_cheekR);
        AddFace(Stage, 10.5, 17.5, 13.5, 2.8, 3.6, ink, ink, 1.4, 0.8);
    }

    protected override void ApplyMood(MascotMood mood)
    {
        double blush = mood is MascotMood.Happy or MascotMood.Vibing ? 0.95 : mood == MascotMood.Sleepy ? 0.3 : 0.55;
        var fade = new DoubleAnimation(blush, TimeSpan.FromMilliseconds(400));
        _cheekL.BeginAnimation(OpacityProperty, fade);
        _cheekR.BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnTick(double beat)
    {
        // A gentle jelly wobble while idle.
        if (Mood is MascotMood.Idle or MascotMood.Curious)
            Squash.ScaleX = 1 + Math.Sin(Time * 2.2) * 0.02;
    }
}

/// <summary>Sprout: a seedling in a terracotta pot. Leaves sway, droop and bloom.</summary>
public sealed class SproutView : CharacterBase
{
    private readonly RotateTransform _leafL = new() { CenterX = 14, CenterY = 9 }, _leafR = new() { CenterX = 14, CenterY = 8 };
    private readonly ScaleTransform _bloom = new() { CenterX = 14, CenterY = 3.5, ScaleX = 0, ScaleY = 0 };
    private double _l, _r;

    public SproutView()
    {
        var leaf = Solid("#5BD06A");
        var stem = Solid("#3FA554");
        Stage.Children.Add(Shape("M 14,14.5 C 14,11 14,9 14,6.5", null, stem, 1.6));

        var left = Shape("M 14,9 C 10,9.5 6.5,7.5 6,4 C 10,3.5 13,5.5 14,9 Z", leaf);
        left.RenderTransform = _leafL;
        var right = Shape("M 14,8 C 18,8.5 21.5,6.5 22,3 C 18,2.5 15,4.5 14,8 Z", leaf);
        right.RenderTransform = _leafR;
        Stage.Children.Add(left);
        Stage.Children.Add(right);

        // The flower that appears on good news.
        var flower = new Canvas { RenderTransform = _bloom };
        var petal = Solid("#FFD1DC");
        for (int i = 0; i < 5; i++)
        {
            double a = i * Math.PI * 2 / 5 - Math.PI / 2;
            flower.Children.Add(Dot(14 + Math.Cos(a) * 2.6 - 1.8, 3.5 + Math.Sin(a) * 2.6 - 1.8, 3.6, 3.6, petal));
        }
        flower.Children.Add(Dot(12.6, 2.1, 2.8, 2.8, Solid("#FFC83D")));
        Stage.Children.Add(flower);

        // Pot with a face.
        Stage.Children.Add(Shape("M 7.5,15.5 L 20.5,15.5 L 19,24 L 9,24 Z", Solid("#D9784A")));
        Stage.Children.Add(Shape("M 6.5,13.5 L 21.5,13.5 L 21.5,16 L 6.5,16 Z", Solid("#E98C5D")));
        var ink = Solid("#3A1F12");
        AddFace(Stage, 11.6, 16.4, 19.3, 2.1, 2.6, ink, ink, 1.2, 0.6);
    }

    protected override void ApplyMood(MascotMood mood)
    {
        var to = mood == MascotMood.Happy ? 1.0 : 0.0;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(mood == MascotMood.Happy ? 500 : 300))
        {
            EasingFunction = mood == MascotMood.Happy ? new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } : null,
        };
        _bloom.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _bloom.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    protected override void OnTick(double beat)
    {
        // Base pose per mood (negative left / positive right = drooping), plus a sway.
        var (baseL, baseR, amp, speed) = Mood switch
        {
            MascotMood.Sleepy => (-32.0, 32.0, 1.0, 1.0),
            MascotMood.Alarmed => (14.0, -14.0, 2.0, 6.0),
            MascotMood.Curious => (0.0, -24.0, 3.0, 2.0),
            MascotMood.Focused => (0.0, 0.0, 1.0, 1.0),
            MascotMood.Vibing => (0.0, 0.0, 6 + beat * 22, 5.0),
            MascotMood.Dizzy => (0.0, 0.0, 18.0, 9.0),
            _ => (0.0, 0.0, 4.0, 1.6),
        };
        double sway = Math.Sin(Time * speed) * amp;
        _l += (baseL + sway - _l) * 0.3;
        _r += (baseR + sway - _r) * 0.3;
        _leafL.Angle = _l;
        _leafR.Angle = _r;
    }
}

/// <summary>Orbit: a little planet in the accent colour with a moon going round it.</summary>
public sealed class OrbitView : CharacterBase
{
    private readonly Ellipse _moon;
    private double _angle, _speed = 1.2, _radius = 12.5;

    public OrbitView()
    {
        var planet = new Ellipse { Width = 18, Height = 18 };
        planet.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        Canvas.SetLeft(planet, 5);
        Canvas.SetTop(planet, 5.5);
        Panel.SetZIndex(planet, 1);
        Stage.Children.Add(planet);

        var shine = Dot(8, 8, 5, 3.5, Solid("#40FFFFFF"));
        Panel.SetZIndex(shine, 1);
        Stage.Children.Add(shine);

        var ring = new Ellipse { Width = 27, Height = 7, Stroke = Solid("#8CFFFFFF"), StrokeThickness = 1.1 };
        Canvas.SetLeft(ring, 0.5);
        Canvas.SetTop(ring, 11.5);
        Panel.SetZIndex(ring, 2);
        Stage.Children.Add(ring);

        _moon = Dot(0, 0, 5, 5, Solid("#F2F2F7"));
        Stage.Children.Add(_moon);

        var face = new Canvas();
        Panel.SetZIndex(face, 3);
        Stage.Children.Add(face);
        var white = Solid("#FFFFFF");
        AddFace(face, 11.3, 16.7, 13.6, 2.6, 3.4, white, white, 1.3, 0.7);
    }

    protected override void ApplyMood(MascotMood mood) { }

    protected override void OnTick(double beat)
    {
        double target = Mood switch
        {
            MascotMood.Sleepy => 0,
            MascotMood.Focused => 4.5,
            MascotMood.Vibing => 2 + beat * 7,
            MascotMood.Happy => 8,
            MascotMood.Dizzy => 10,
            MascotMood.Curious => 0.6,
            MascotMood.Alarmed => 0.3,
            _ => 1.2,
        };
        _speed += (target - _speed) * 0.1;
        _radius += ((Mood == MascotMood.Alarmed ? 15.5 : 12.5) - _radius) * 0.2;
        _angle += _speed * 0.05;

        double x = 14 + Math.Cos(_angle) * _radius, y = 14.5 + Math.Sin(_angle) * 4.2;
        Canvas.SetLeft(_moon, x - 2.5);
        Canvas.SetTop(_moon, y - 2.5);
        bool front = Math.Sin(_angle) > 0;
        Panel.SetZIndex(_moon, front ? 4 : 0);
        _moon.Opacity = Mood == MascotMood.Sleepy ? 0.35 : front ? 1 : 0.7;
    }
}
