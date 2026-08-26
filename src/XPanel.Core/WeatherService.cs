using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XPanel.Core.Weather
{
    /// <summary>
    /// 未来某一天的天气（与设备端 WeatherData.futureDays 对齐）。
    /// </summary>
    public sealed class WeatherFutureDay
    {
        public float TempMinC { get; set; }
        public float TempMaxC { get; set; }
        public byte WeatherCode { get; set; }
    }

    /// <summary>
    /// 天气数据，字段与设备端 <c>WeatherData</c> 一一对应，用于 wx_mode=1 同步。
    /// </summary>
    public sealed class WeatherData
    {
        public const int MaxFutureDays = 2;

        public bool Valid { get; set; }
        public bool HasTemperature { get; set; }
        public bool HasWeatherCode { get; set; }
        public string City { get; set; } = string.Empty;
        public float TemperatureC { get; set; }
        public byte WeatherCode { get; set; }
        public byte FutureDayCount { get; set; }
        public WeatherFutureDay[] FutureDays { get; } =
        {
            new WeatherFutureDay(),
            new WeatherFutureDay(),
        };

        /// <summary>
        /// 生成变更检测签名，仅包含协议实际下发的量化值（温度按 x10 定点），
        /// 保证“可下发内容一致”时不会重复发送。
        /// </summary>
        public string BuildChangeSignature()
        {
            var sb = new StringBuilder(64);
            sb.Append(Valid ? '1' : '0').Append('|');
            sb.Append(HasTemperature ? '1' : '0').Append('|');
            sb.Append(HasWeatherCode ? '1' : '0').Append('|');
            sb.Append(City).Append('|');
            sb.Append(ToTempX10(TemperatureC)).Append('|');
            sb.Append(WeatherCode).Append('|');
            sb.Append(FutureDayCount);
            for (int i = 0; i < FutureDayCount && i < MaxFutureDays; i++)
            {
                var day = FutureDays[i];
                sb.Append('|')
                  .Append(ToTempX10(day.TempMinC)).Append(',')
                  .Append(ToTempX10(day.TempMaxC)).Append(',')
                  .Append(day.WeatherCode);
            }

            return sb.ToString();
        }

        public static short ToTempX10(float celsius)
        {
            int scaled = (int)Math.Round(celsius * 10.0f, MidpointRounding.AwayFromZero);
            if (scaled > short.MaxValue)
            {
                scaled = short.MaxValue;
            }
            else if (scaled < short.MinValue)
            {
                scaled = short.MinValue;
            }

            return (short)scaled;
        }
    }

    /// <summary>
    /// 天气获取服务。与设备端 <c>WeatherService</c> 使用相同的 Open-Meteo 接口与解析逻辑，
    /// 以保证控制端与设备端天气数据完全一致。
    /// </summary>
    public sealed class WeatherService
    {
        private const string GeocodingUrlFormat =
            "https://geocoding-api.open-meteo.com/v1/search?name={0}&count=1&language=zh&format=json&country=CN";
        private const string ForecastUrlFormat =
            "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current_weather=true&daily=weathercode,temperature_2m_max,temperature_2m_min&forecast_days=3&timezone=auto";

        private static readonly HttpClient HttpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XPanelClient/1.0");
            return client;
        }

        /// <summary>
        /// 按城市名拉取当前天气与未来两天预报。失败时返回 Valid=false 的对象。
        /// </summary>
        public async Task<WeatherData> GetWeatherAsync(string cityName, CancellationToken cancellationToken = default)
        {
            var result = new WeatherData();
            if (string.IsNullOrWhiteSpace(cityName))
            {
                return result;
            }

            string requestCity = cityName.Trim();
            result.City = requestCity;

            string geocodingCity = NormalizeGeocodingCity(requestCity);
            string geoUrl = string.Format(
                CultureInfo.InvariantCulture,
                GeocodingUrlFormat,
                Uri.EscapeDataString(geocodingCity));

            (float latitude, float longitude, string resolvedName)? location =
                await ResolveLocationAsync(geoUrl, cancellationToken);
            if (location == null)
            {
                return result;
            }

            result.City = location.Value.resolvedName;

            string forecastUrl = string.Format(
                CultureInfo.InvariantCulture,
                ForecastUrlFormat,
                location.Value.latitude.ToString("0.0000", CultureInfo.InvariantCulture),
                location.Value.longitude.ToString("0.0000", CultureInfo.InvariantCulture));

            string? forecastBody = await TryGetStringAsync(forecastUrl, cancellationToken);
            if (forecastBody == null)
            {
                return result;
            }

            ParseForecast(forecastBody, result);
            return result;
        }

        private static async Task<(float latitude, float longitude, string resolvedName)?> ResolveLocationAsync(
            string geoUrl,
            CancellationToken cancellationToken)
        {
            string? body = await TryGetStringAsync(geoUrl, cancellationToken);
            if (body == null)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array ||
                    results.GetArrayLength() == 0)
                {
                    return null;
                }

                var first = results[0];
                float lat = ReadSingle(first, "latitude", float.NaN);
                float lon = ReadSingle(first, "longitude", float.NaN);
                if (float.IsNaN(lat) || float.IsNaN(lon))
                {
                    return null;
                }

                string name = first.TryGetProperty("name", out var nameEl) &&
                              nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;

                return (lat, lon, name);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void ParseForecast(string body, WeatherData result)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;

                if (!TryGetObject(root, "current_weather", out var current))
                {
                    TryGetObject(root, "current", out current);
                }

                if (current.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                if (TryReadNumber(current, "temperature", out float temperature) ||
                    TryReadNumber(current, "temperature_2m", out temperature))
                {
                    if (!float.IsNaN(temperature) && !float.IsInfinity(temperature))
                    {
                        result.HasTemperature = true;
                        result.TemperatureC = temperature;
                    }
                }

                if (TryReadNumber(current, "weathercode", out float code) ||
                    TryReadNumber(current, "weather_code", out code))
                {
                    int codeInt = (int)code;
                    if (codeInt >= 0 && codeInt <= 255)
                    {
                        result.HasWeatherCode = true;
                        result.WeatherCode = (byte)codeInt;
                    }
                }

                ParseDaily(root, result);

                result.Valid = true;
            }
        }

        private static void ParseDaily(JsonElement root, WeatherData result)
        {
            if (!TryGetObject(root, "daily", out var daily))
            {
                return;
            }

            if (!(TryGetArray(daily, "weathercode", out var codes) ||
                  TryGetArray(daily, "weather_code", out codes)))
            {
                return;
            }

            if (!TryGetArray(daily, "temperature_2m_max", out var maxTemps) ||
                !TryGetArray(daily, "temperature_2m_min", out var minTemps))
            {
                return;
            }

            int available = Math.Min(codes.GetArrayLength(),
                Math.Min(maxTemps.GetArrayLength(), minTemps.GetArrayLength()));

            byte futureCount = 0;
            // 索引 0 是今天，从 1 开始取未来日，最多两天。
            for (int i = 1; i < available && futureCount < WeatherData.MaxFutureDays; i++)
            {
                var day = result.FutureDays[futureCount];
                day.WeatherCode = ReadDailyCode(codes[i]);
                day.TempMaxC = ReadDailyTemp(maxTemps[i], result.TemperatureC);
                day.TempMinC = ReadDailyTemp(minTemps[i], result.TemperatureC);
                futureCount++;
            }

            result.FutureDayCount = futureCount;
        }

        private static string NormalizeGeocodingCity(string cityName)
        {
            string[] suffixes = { "市", "县", "区" };
            foreach (string suffix in suffixes)
            {
                if (cityName.Length > suffix.Length && cityName.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return cityName.Substring(0, cityName.Length - suffix.Length);
                }
            }

            return cityName;
        }

        private static async Task<string?> TryGetStringAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await HttpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }

        private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
        {
            if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            value = default;
            return false;
        }

        private static bool TryGetArray(JsonElement parent, string name, out JsonElement value)
        {
            if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            value = default;
            return false;
        }

        private static bool TryReadNumber(JsonElement parent, string name, out float value)
        {
            value = float.NaN;
            if (!parent.TryGetProperty(name, out var element))
            {
                return false;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out float parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }

        private static float ReadSingle(JsonElement parent, string name, float fallback)
        {
            if (parent.TryGetProperty(name, out var element) &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetSingle(out float value))
            {
                return value;
            }

            return fallback;
        }

        private static byte ReadDailyCode(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int code) &&
                code >= 0 && code <= 255)
            {
                return (byte)code;
            }

            return 0;
        }

        private static float ReadDailyTemp(JsonElement element, float fallback)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out float value) &&
                !float.IsNaN(value) && !float.IsInfinity(value))
            {
                return value;
            }

            return fallback;
        }
    }
}
