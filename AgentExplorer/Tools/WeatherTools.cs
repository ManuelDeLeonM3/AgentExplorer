using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentExplorer.Tools;

/// <summary>Weather and temperature tools. Weather data comes from the free Open-Meteo API (no key needed).</summary>
public static class WeatherTools
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentExplorer/1.0");
        return http;
    }

    public static IAgentTool GetCurrentWeather() => new DelegateTool(
        "get_current_weather",
        "Gets the current weather for a city. All temperatures are returned in degrees Celsius.",
        """
        {
          "type": "object",
          "properties": {
            "city": { "type": "string", "description": "City name, e.g. Tokyo" },
            "country": { "type": "string", "description": "Country name, e.g. Japan" }
          },
          "required": ["city"]
        }
        """,
        GetWeatherAsync);

    public static IAgentTool ConvertTemperature() => new DelegateTool(
        "convert_temperature",
        "Converts a temperature between Celsius (C) and Fahrenheit (F).",
        """
        {
          "type": "object",
          "properties": {
            "value": { "type": "number", "description": "The temperature to convert" },
            "from_unit": { "type": "string", "enum": ["C", "F"] },
            "to_unit": { "type": "string", "enum": ["C", "F"] }
          },
          "required": ["value", "from_unit", "to_unit"]
        }
        """,
        (args, _) =>
        {
            var value = args.GetDouble("value") ?? throw new ArgumentException("'value' is required.");
            var from = (args.GetString("from_unit") ?? "C").Trim().ToUpperInvariant()[..1];
            var to = (args.GetString("to_unit") ?? "F").Trim().ToUpperInvariant()[..1];
            double result = (from, to) switch
            {
                ("C", "F") => value * 9 / 5 + 32,
                ("F", "C") => (value - 32) * 5 / 9,
                _ => value,
            };
            return Task.FromResult(ToolExtensions.ToJson(new
            {
                input = $"{value.ToString("0.#", CultureInfo.InvariantCulture)} °{from}",
                result = Math.Round(result, 1),
                unit = to,
            }));
        });

    private static async Task<string> GetWeatherAsync(JsonElement args, CancellationToken ct)
    {
        var city = args.GetString("city")?.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(city)) throw new ArgumentException("'city' is required.");
        var country = args.GetString("country")?.Trim();

        var geo = await Http.GetFromJsonAsync<JsonElement>(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=10&language=en&format=json", ct);
        if (!geo.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return ToolExtensions.ToJson(new { error = $"Could not find a city named '{city}'." });
        }

        var place = results.EnumerateArray().FirstOrDefault(r =>
            string.IsNullOrEmpty(country) ||
            string.Equals(r.GetString("country"), country, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.GetString("country_code"), country, StringComparison.OrdinalIgnoreCase));
        if (place.ValueKind == JsonValueKind.Undefined) place = results[0];

        var lat = place.GetProperty("latitude").GetDouble().ToString(CultureInfo.InvariantCulture);
        var lon = place.GetProperty("longitude").GetDouble().ToString(CultureInfo.InvariantCulture);
        var weather = await Http.GetFromJsonAsync<JsonElement>(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code&temperature_unit=celsius&wind_speed_unit=kmh&timezone=auto", ct);
        var current = weather.GetProperty("current");

        return ToolExtensions.ToJson(new
        {
            location = place.GetString("name"),
            country = place.GetString("country"),
            observed_at = current.GetString("time"),
            temperature_c = current.GetDouble("temperature_2m"),
            feels_like_c = current.GetDouble("apparent_temperature"),
            humidity_percent = current.GetDouble("relative_humidity_2m"),
            wind_kmh = current.GetDouble("wind_speed_10m"),
            conditions = Describe(current.GetInt("weather_code") ?? -1),
        });
    }

    private static string Describe(int code) => code switch
    {
        0 => "Clear sky",
        1 or 2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Rain showers",
        85 or 86 => "Snow showers",
        >= 95 => "Thunderstorm",
        _ => "Unknown",
    };
}
