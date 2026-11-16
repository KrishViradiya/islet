using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Islet.Core;

namespace Islet.Modules;

/// <summary>
/// Synced (karaoke-style) lyrics for whatever is playing, from LRCLIB (lrclib.net) — a free,
/// open lyrics database that needs no API key. Only the song title and artist are sent.
/// </summary>
public sealed class LyricsModule
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex Stamp = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

    private List<(double Time, string Text)> _lines = new();
    private string? _key;

    public bool HasLines => _lines.Count > 0;

    public async Task LoadAsync(string title, string artist, double durationSeconds)
    {
        var key = $"{artist}\n{title}";
        if (key == _key) return;
        _key = key;
        _lines = new();
        if (string.IsNullOrWhiteSpace(title)) return;

        try
        {
            // 1) Exact search — works for Spotify / Apple Music, which report clean titles and artists.
            var query = $"track_name={Uri.EscapeDataString(title)}"
                        + (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");
            var best = await SearchAsync(query, durationSeconds);

            // 2) Video titles ("Lyrical | Song Name | Movie | Singer") → try the cleaned-up pieces.
            foreach (var candidate in Candidates(title).Take(3))
            {
                if (best != null || _key != key) break;
                best = await SearchAsync($"q={Uri.EscapeDataString(candidate)}", durationSeconds);
            }
            if (best == null) return;

            var parsed = Parse(best);
            if (_key == key) _lines = parsed; // still the same song?
        }
        catch (Exception ex) { Log.Error("lyrics", ex); }
    }

    private static async Task<string?> SearchAsync(string query, double durationSeconds)
    {
        using var response = await Http.GetAsync($"https://lrclib.net/api/search?{query}");
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        // Prefer the result whose length matches the playing track (avoids live/remix versions).
        string? best = null;
        double bestDiff = double.MaxValue;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("syncedLyrics", out var synced) || synced.ValueKind != JsonValueKind.String) continue;
            double length = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            double diff = durationSeconds > 0 && length > 0 ? Math.Abs(length - durationSeconds) : 0;
            if (diff < bestDiff) { bestDiff = diff; best = synced.GetString(); }
        }
        return bestDiff <= 12 ? best : null;
    }

    private static readonly Regex Brackets = new(@"[\(\[\{【].*?[\)\]\}】]", RegexOptions.Compiled);
    private static readonly Regex Noise = new(
        @"\b(official|lyrical|lyrics?|lyric video|video|audio|music|full song|song|hd|4k|visuali[sz]er|remastered|feat\.?|ft\.?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Pieces of a messy video title worth searching for, most promising first.</summary>
    private static IEnumerable<string> Candidates(string title)
    {
        var cleaned = Noise.Replace(Brackets.Replace(title, " "), " ");
        var parts = cleaned.Split(new[] { '|', '-', '–', '—', ':', '•' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(p => Regex.Replace(p, @"\s+", " ").Trim(' ', '.', '"', '\''))
                           .Where(p => p.Length >= 3)
                           .ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts)
            if (seen.Add(p)) yield return p;
    }

    /// <summary>The line being sung at <paramref name="seconds"/> and the one after it.</summary>
    public (string Current, string Next) At(double seconds)
    {
        var lines = _lines;
        int index = -1;
        for (int i = 0; i < lines.Count && lines[i].Time <= seconds; i++) index = i;
        string current = index >= 0 ? lines[index].Text : "";
        string next = index + 1 < lines.Count ? lines[index + 1].Text : "";
        return (current, next);
    }

    private static List<(double, string)> Parse(string lrc)
    {
        var result = new List<(double, string)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var matches = Stamp.Matches(raw);
            if (matches.Count == 0) continue;
            var text = raw[(matches[^1].Index + matches[^1].Length)..].Trim();
            if (text.Length == 0) text = "♪";
            foreach (Match m in matches)
            {
                double t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                           + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                result.Add((t, text));
            }
        }
        return result.OrderBy(l => l.Item1).ToList();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Islet/0.2 (Windows dynamic island)");
        return client;
    }
}
