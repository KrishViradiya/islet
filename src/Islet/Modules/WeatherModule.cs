using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Islet.Core;

namespace Islet.Modules;

public sealed record WeatherNow(double Temperature, string Glyph, string Summary, bool RainSoon, string RainText);

/// <summary>
/// Current temperature + "rain in ~30 min" from Open-Meteo (free, no API key). Opt-in: only the
/// city name you type (for geocoding) and its coordinates are sent.
/// </summary>
public sealed class WeatherModule
{
    private static readonly HttpClient Http = CreateClient();
    private string? _city;
    private (double lat, double lon, string name)? _place;

    public string PlaceName => _place?.name ?? "";

    public async Task<WeatherNow?> FetchAsync(string city, bool fahrenheit)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        try
        {
            if (_city != city || _place == null)
            {
                _place = await GeocodeAsync(city);
                _city = city;
            }
            if (_place is not { } p) return null;

            var inv = CultureInfo.InvariantCulture;
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={p.lat.ToString(inv)}&longitude={p.lon.ToString(inv)}"
                      + "&current=temperature_2m,weather_code,is_day&minutely_15=precipitation&forecast_minutely_15=4&timezone=auto"
                      + (fahrenheit ? "&temperature_unit=fahrenheit" : "");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            double temp = current.GetProperty("temperature_2m").GetDouble();
            int code = current.GetProperty("weather_code").GetInt32();
            bool day = !current.TryGetProperty("is_day", out var d) || d.GetInt32() == 1;

            // Rain in the next hour (15-minute slots) while it isn't raining yet.
            int slot = -1;
            if (doc.RootElement.TryGetProperty("minutely_15", out var m) && m.TryGetProperty("precipitation", out var precip))
            {
                var values = precip.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0).ToList();
                slot = values.FindIndex(v => v >= 0.1);
            }
            bool rainingNow = IsWet(code);
            bool rainSoon = !rainingNow && slot >= 0;
            var rainText = rainSoon ? (slot == 0 ? "Rain any minute" : $"Rain in ~{slot * 15} min") : "";

            return new WeatherNow(temp, Glyph(code, day), Describe(code), rainSoon, rainText);
        }
        catch (Exception ex)
        {
            Log.Error("weather", ex);
            return null;
        }
    }

    private static async Task<(double, double, string)?> GeocodeAsync(string city)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city.Trim())}&count=1";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), r.GetProperty("name").GetString() ?? city);
    }

    private static bool IsWet(int code) => code is >= 51 and <= 67 or >= 80 and <= 82 or >= 95;

    /// <summary>WMO weather code → a Segoe UI Symbol glyph.</summary>
    private static string Glyph(int code, bool day) => code switch
    {
        0 => day ? "☀" : "☾",
        1 or 2 => day ? "⛅" : "☾",
        3 or 45 or 48 => "☁",
        >= 51 and <= 67 or >= 80 and <= 82 => "☂",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 => "⚡",
        _ => "☁",
    };

    private static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        >= 95 => "Thunderstorm",
        _ => "",
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Islet/0.3 (Windows dynamic island)");
        return client;
    }
}
