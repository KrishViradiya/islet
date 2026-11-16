using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Islet;

public enum MascotMood { Idle, Vibing, Focused, Curious, Happy, Sleepy, Dizzy, Alarmed }

/// <summary>
/// Blip, the island's resident. Moods come from the view model; <see cref="Tick"/> is called ~20×/s by
/// the window with where the cursor is (for the eyes) and how loud the music is (for the bob).
/// </summary>
public partial class MascotView : UserControl
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(MascotMood), typeof(MascotView),
        new PropertyMetadata(MascotMood.Idle, (d, _) => ((MascotView)d).ApplyMood()));

    private readonly Random _rng = new();
    private DateTime _nextBlink = DateTime.Now.AddSeconds(2);
    private double _gazeX, _gazeY, _spin;

    public MascotMood Mood
    {
        get => (MascotMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    public event Action? Poked;

    public MascotView()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            Squish();
            Poked?.Invoke();
            e.Handled = true;
        };
    }

    /// <param name="look">Direction to the cursor, each axis -1..1.</param>
    /// <param name="beat">Music loudness 0..1.</param>
    public void Tick(Vector look, double beat)
    {
        if (!IsVisible) return;
        var mood = Mood;

        // Where the eyes want to look.
        var (tx, ty) = mood switch
        {
            MascotMood.Focused => (look.X * 0.8, 1.1),        // eyes down on the work
            MascotMood.Curious => (1.3, -0.9),                // glancing up at the "?"
            MascotMood.Sleepy or MascotMood.Happy => (0.0, 0.0),
            _ => (look.X * 1.7, look.Y * 1.0),
        };

        if (mood == MascotMood.Dizzy)
        {
            _spin += 0.55;
            tx = Math.Cos(_spin) * 1.6;
            ty = Math.Sin(_spin) * 1.0;
            Tilt.Angle = Math.Sin(_spin * 0.45) * 12;
        }

        _gazeX += (tx - _gazeX) * 0.35;
        _gazeY += (ty - _gazeY) * 0.35;
        Gaze.X = _gazeX;
        Gaze.Y = _gazeY;

        if (mood == MascotMood.Vibing)
        {
            Hop.Y = -beat * 3.2;
            Squash.ScaleY = 1 - beat * 0.07;
            Squash.ScaleX = 1 + beat * 0.05;
        }

        if (DateTime.Now >= _nextBlink && OpenEyes.Visibility == Visibility.Visible && mood != MascotMood.Focused)
        {
            Blink();
            _nextBlink = DateTime.Now.AddSeconds(2.2 + _rng.NextDouble() * 4);
        }
    }

    private void ApplyMood()
    {
        var mood = Mood;
        OpenEyes.Visibility = mood is MascotMood.Happy or MascotMood.Sleepy or MascotMood.Dizzy ? Visibility.Collapsed : Visibility.Visible;
        HappyEyes.Visibility = mood == MascotMood.Happy ? Visibility.Visible : Visibility.Collapsed;
        SleepEyes.Visibility = mood == MascotMood.Sleepy ? Visibility.Visible : Visibility.Collapsed;
        DizzyEyes.Visibility = mood == MascotMood.Dizzy ? Visibility.Visible : Visibility.Collapsed;

        // Reset anything a previous mood left running.
        Hop.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        Squash.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        Squash.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        Lids.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        Lids.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        Hop.Y = 0;
        Squash.ScaleX = Squash.ScaleY = 1;
        Lids.ScaleX = 1;
        Lids.ScaleY = mood == MascotMood.Focused ? 0.55 : 1;   // concentrating squint

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (mood != MascotMood.Dizzy)
            Tilt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new DoubleAnimation(mood == MascotMood.Curious ? 12 : 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });

        // "?" while waiting for you.
        Ask.BeginAnimation(OpacityProperty, new DoubleAnimation(mood == MascotMood.Curious ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        AskBob.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, mood == MascotMood.Curious
            ? new DoubleAnimation(0, -2.5, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);

        // Floating "z" and slow breathing while asleep.
        if (mood == MascotMood.Sleepy)
        {
            var rise = new DoubleAnimation(2, -9, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever };
            ZzzRise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, rise);
            var fade = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.3)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            Zzz.BeginAnimation(OpacityProperty, fade);
            Squash.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.92, TimeSpan.FromSeconds(1.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        }
        else
        {
            ZzzRise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            Zzz.BeginAnimation(OpacityProperty, null);
            Zzz.Opacity = 0;
        }

        if (mood == MascotMood.Happy)
        {
            var hop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromPercent(0.22), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.45), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(-3.5, KeyTime.FromPercent(0.65), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.85), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            Hop.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, hop);
        }

        if (mood == MascotMood.Alarmed)
        {
            var wide = new DoubleAnimation(1, 1.3, TimeSpan.FromMilliseconds(160)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
            Lids.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, wide);
            Lids.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, wide);
        }
    }

    private void Blink()
    {
        var baseline = Lids.ScaleY;
        Lids.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(baseline, 0.1, TimeSpan.FromMilliseconds(70)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>A little squash when poked.</summary>
    private void Squish()
    {
        if (Mood is MascotMood.Sleepy or MascotMood.Vibing) return;
        var y = new DoubleAnimation(1, 0.78, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop };
        var x = new DoubleAnimation(1, 1.14, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop };
        Squash.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, y);
        Squash.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, x);
    }
}
