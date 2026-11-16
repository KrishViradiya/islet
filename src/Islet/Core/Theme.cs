using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Islet.Core;

/// <summary>
/// Theme colours live in Application.Resources and are referenced with {DynamicResource ...},
/// so swapping a brush here repaints every window instantly.
/// </summary>
public static class ThemeManager
{
    private static readonly Color DefaultAccent = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly TimeSpan AccentTween = TimeSpan.FromMilliseconds(700);
    private static Color _accentColor = DefaultAccent, _shown = DefaultAccent, _from = DefaultAccent;
    private static DateTime _tweenStart;
    private static bool _tweening;
    private static ResourceDictionary? _resources;

    /// <summary>Current accent brush (album colour or the user's pick).</summary>
    public static Brush Accent => (Brush)(_resources?["Accent"] ?? Brushes.DodgerBlue);

    public static bool IsLight { get; private set; }
    public static Color AccentColor => _accentColor;
    public static event Action? Changed;

    public static void Init(ResourceDictionary resources)
    {
        _resources = resources;
        PublishAccent(DefaultAccent);
    }

    public static void Apply(ThemeMode mode)
    {
        IsLight = mode == ThemeMode.Light || (mode == ThemeMode.System && SystemUsesLightTheme());
        var r = Application.Current.Resources;
        if (IsLight)
        {
            Set(r, "IslandBg", "#F5F5F7");
            Set(r, "Fg", "#1C1C1E");
            Set(r, "Dim", "#8C1C1C1E");
            Set(r, "Faint", "#5A1C1C1E");
            Set(r, "Card", "#0E000000");
            Set(r, "CardHover", "#18000000");
            Set(r, "Pressed", "#26000000");
            Set(r, "Track", "#1F000000");
            Set(r, "ToggleOff", "#2E000000");
            Set(r, "PanelBg", "#F7F7F9");
            Set(r, "NavBg", "#EDEDF1");
            Set(r, "Divider", "#12000000");
            Set(r, "Inverse", "#1C1C1E");
            Set(r, "InverseFg", "#FFFFFF");
        }
        else
        {
            Set(r, "IslandBg", "#000000");
            Set(r, "Fg", "#FFFFFF");
            Set(r, "Dim", "#A3FFFFFF");
            Set(r, "Faint", "#5EFFFFFF");
            Set(r, "Card", "#16FFFFFF");
            Set(r, "CardHover", "#24FFFFFF");
            Set(r, "Pressed", "#38FFFFFF");
            Set(r, "Track", "#30FFFFFF");
            Set(r, "ToggleOff", "#3DFFFFFF");
            Set(r, "PanelBg", "#1A1A1D");
            Set(r, "NavBg", "#131315");
            Set(r, "Divider", "#12FFFFFF");
            Set(r, "Inverse", "#FFFFFF");
            Set(r, "InverseFg", "#000000");
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Cross-fades the accent. WPF freezes brushes that templates share, so instead of animating one
    /// brush we publish a fresh frozen brush each frame; {DynamicResource Accent} picks it up everywhere.
    /// </summary>
    public static void SetAccent(Color color)
    {
        if (color == _accentColor) return;
        _accentColor = color;
        _from = _shown;
        _tweenStart = DateTime.Now;
        if (!_tweening)
        {
            _tweening = true;
            CompositionTarget.Rendering += TweenAccent;
        }
        Changed?.Invoke();
    }

    private static void TweenAccent(object? sender, EventArgs e)
    {
        double t = Math.Clamp((DateTime.Now - _tweenStart).TotalMilliseconds / AccentTween.TotalMilliseconds, 0, 1);
        double k = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease-in-out cubic
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * k);
        PublishAccent(Color.FromRgb(Mix(_from.R, _accentColor.R), Mix(_from.G, _accentColor.G), Mix(_from.B, _accentColor.B)));
        if (t >= 1)
        {
            CompositionTarget.Rendering -= TweenAccent;
            _tweening = false;
        }
    }

    private static void PublishAccent(Color c)
    {
        if (_resources == null) return;
        _shown = c;
        var solid = new SolidColorBrush(c);
        solid.Freeze();
        var wash = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x66, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 0.9),
            },
        };
        wash.Freeze();
        _resources["Accent"] = solid;
        _resources["AccentWash"] = wash;
    }

    public static Color Parse(string? hex, Color? fallback = null)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? "")!; }
        catch { return fallback ?? DefaultAccent; }
    }

    public static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }

    private static void Set(ResourceDictionary r, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        r[key] = brush;
    }
}

/// <summary>Picks a vivid representative colour from album art (for the accent and glow).</summary>
public static class ColorUtil
{
    public static Color? FromArtwork(byte[] imageBytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = new MemoryStream(imageBytes);
            image.EndInit();

            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            // Weight each pixel by how colourful and bright it is, so dull backgrounds don't win.
            double rs = 0, gs = 0, bs = 0, ws = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double weight = sat * sat * max + 0.003;
                rs += r * weight; gs += g * weight; bs += b * weight; ws += weight;
            }

            var (hue, s, l) = ToHsl(rs / ws, gs / ws, bs / ws);
            if (s < 0.12) return null; // greyscale artwork: keep the user's accent
            return FromHsl(hue, Math.Max(s, 0.62), Math.Clamp(l, 0.55, 0.66));
        }
        catch { return null; }
    }

    private static (double h, double s, double l) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0, d = max - min;
        if (d > 1e-6)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        byte C(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double v = t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
            return (byte)Math.Round(v * 255);
        }
        return Color.FromRgb(C(h + 1 / 3.0), C(h), C(h - 1 / 3.0));
    }
}
