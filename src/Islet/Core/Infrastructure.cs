using System;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace Islet.Core;

public static class Palette
{
    public static readonly Brush Blue = Make(0x0A, 0x84, 0xFF);
    public static readonly Brush Green = Make(0x30, 0xD1, 0x58);
    public static readonly Brush Red = Make(0xFF, 0x45, 0x3A);
    public static readonly Brush Orange = Make(0xFF, 0x9F, 0x0A);
    public static readonly Brush Pink = Make(0xFF, 0x37, 0x5F);
    public static readonly Brush Gray = Make(0x8E, 0x8E, 0x93);
    public static readonly Brush Transparent = Brushes.Transparent;

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze(); // frozen brushes are thread-safe and cheaper to render
        return brush;
    }
}

public static class Paths
{
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islet");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Data(string file) => Path.Combine(DataDir, file);
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);
    public static void Error(string where, Exception ex) => Write("ERROR", $"{where}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Paths.Data("islet.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}

/// <summary>"Start with Windows" via the per-user Run key (no admin needed).</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Islet") is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("Islet", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("Islet", throwOnMissingValue: false);
    }
}
