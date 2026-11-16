using System;

namespace Islet.Core;

/// <summary>
/// The island's lower edge as a little elastic membrane: a row of points joined by springs.
/// Each point is pulled toward a target shape (cursor bulge, music ripple, sag) and toward its
/// neighbours (surface tension), so pokes travel as waves and everything settles smoothly.
/// Units are DIPs; positive values push the edge downward.
/// </summary>
public sealed class LiquidEngine
{
    public const int Nodes = 30;

    private readonly double[] _y = new double[Nodes], _v = new double[Nodes], _target = new double[Nodes];
    private double _sag, _phase;

    /// <summary>Cursor position along the edge (0 = left, 1 = right) and how strongly it pulls (0..1).</summary>
    public double CursorAt = 0.5, CursorPull;
    /// <summary>Smoothed loudness 0..1 — drives the music ripple.</summary>
    public double Music;
    /// <summary>0..1 — how much the middle droops ("sags when you're away").</summary>
    public double SagTarget;

    public double this[int i] => _y[i];

    public bool IsQuiet
    {
        get
        {
            if (CursorPull > 0.01 || Music > 0.01 || Math.Abs(_sag - SagTarget) > 0.01) return false;
            for (int i = 0; i < Nodes; i++)
                if (Math.Abs(_y[i] - _target[i]) > 0.03 || Math.Abs(_v[i]) > 0.05) return false;
            return true;
        }
    }

    /// <summary>A sudden push (e.g. a bead pulling away, a poke). Positive = downward.</summary>
    public void Kick(double at, double velocity, double width = 0.12)
    {
        for (int i = 1; i < Nodes - 1; i++)
        {
            double u = i / (double)(Nodes - 1);
            _v[i] += velocity * Math.Exp(-Math.Pow((u - at) / width, 2));
        }
    }

    /// <summary>Side-to-side jolt along the whole edge (errors, denied requests).</summary>
    public void Shake(double strength)
    {
        for (int i = 1; i < Nodes - 1; i++) _v[i] += (i % 2 == 0 ? 1 : -1) * strength * Math.Sin(Math.PI * i / (Nodes - 1));
    }

    public void Step(double dt)
    {
        _phase += dt * (1.6 + Music * 3.5);
        _sag += (SagTarget - _sag) * Math.Min(1, dt * 1.2);

        for (int i = 0; i < Nodes; i++)
        {
            double u = i / (double)(Nodes - 1);
            double envelope = Math.Sin(Math.PI * u); // edges stay pinned to the rounded corners
            double t = 0;

            // Reach toward the cursor: a soft bump under it.
            if (CursorPull > 0) t += 9 * CursorPull * Math.Exp(-Math.Pow((u - CursorAt) / 0.17, 2));

            // Music: a travelling "chirp" — long waves on the left (bass), short ones on the right (treble).
            if (Music > 0.003)
            {
                double local = 2 * Math.PI * (u * (1.6 + 2.6 * u)) * 2 - _phase * 2 * Math.PI;
                t += Music * 6.5 * Math.Sin(local) * Math.Pow(envelope, 0.5);
            }

            // Sag: a heavy droop in the middle.
            t += 7 * _sag * envelope;

            _target[i] = t * envelope;
        }

        // Semi-implicit Euler in small sub-steps for stability.
        const double stiffness = 140, tension = 2600, damping = 9;
        while (dt > 1e-6)
        {
            double h = Math.Min(dt, 1 / 240.0);
            for (int i = 1; i < Nodes - 1; i++)
            {
                double laplace = _y[i - 1] + _y[i + 1] - 2 * _y[i];
                double accel = -stiffness * (_y[i] - _target[i]) + tension * laplace / (Nodes * 2.0) - damping * _v[i];
                _v[i] += accel * h;
            }
            for (int i = 1; i < Nodes - 1; i++) _y[i] += _v[i] * h;
            dt -= h;
        }
        _y[0] = _y[Nodes - 1] = 0;
    }
}
