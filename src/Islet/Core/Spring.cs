using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Islet.Core;

/// <summary>
/// A damped spring (Hooke's law + friction). Unlike a fixed-duration animation, a spring keeps its
/// velocity when the target changes mid-flight, so interrupted transitions stay fluid — this is what
/// makes iOS's Dynamic Island feel "liquid".
/// </summary>
public sealed class Spring
{
    public double Value, Velocity, Target;
    public double Stiffness = 260, Damping = 23;

    public bool IsSettled => Math.Abs(Value - Target) < 0.02 && Math.Abs(Velocity) < 0.05;

    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    public void Step(double dt)
    {
        // Small fixed sub-steps keep stiff springs numerically stable at any frame rate.
        while (dt > 1e-6)
        {
            double h = Math.Min(dt, 1 / 240.0);
            double accel = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }
        if (IsSettled) Snap(Target);
    }
}

/// <summary>Steps a group of springs once per rendered frame, and unhooks itself when they all rest.</summary>
public sealed class SpringAnimator
{
    private readonly IReadOnlyList<Spring> _springs;
    private readonly Action _apply;
    private TimeSpan _last;
    private bool _running;

    public SpringAnimator(IEnumerable<Spring> springs, Action apply)
    {
        _springs = springs.ToList();
        _apply = apply;
    }

    public void Kick()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_last == TimeSpan.Zero) { _last = now; return; }
        if (now == _last) return; // Rendering can fire more than once per frame

        double dt = Math.Min((now - _last).TotalSeconds, 0.05);
        _last = now;

        foreach (var s in _springs) s.Step(dt);
        _apply();

        if (_springs.All(s => s.IsSettled))
        {
            CompositionTarget.Rendering -= OnRendering;
            _running = false;
        }
    }
}
