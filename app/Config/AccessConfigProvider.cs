using System.Globalization;
using System.Text.Json;

sealed class AccessConfigProvider
{
    private static readonly string[] DayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private readonly RuntimeSettings _settings;
    private readonly object _sync = new();

    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private long _mtimeMs;
    private AccessConfig? _cached;

    public AccessConfigProvider(RuntimeSettings settings)
    {
        _settings = settings;
    }

    public AccessConfig GetConfig()
    {
        lock (_sync)
        {
            var fi = new FileInfo(_settings.LimitsConfigPath);
            if (!fi.Exists)
            {
                throw new AppHttpException(500, $"Не найден файл конфигурации: {_settings.LimitsConfigPath}");
            }

            var now = DateTimeOffset.UtcNow;
            var currentMtime = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
            // Reuse parsed config while file mtime is unchanged and cache TTL is still valid.
            var fresh = _cached is not null
                && currentMtime == _mtimeMs
                && (now - _loadedAt).TotalMilliseconds < _settings.ConfigCacheTtlMs;

            if (fresh)
            {
                return _cached!;
            }

            var raw = File.ReadAllText(_settings.LimitsConfigPath);
            _cached = ParseConfig(raw, _settings.TimezoneOverride);
            _mtimeMs = currentMtime;
            _loadedAt = now;
            return _cached;
        }
    }

    private static AccessConfig ParseConfig(string raw, string? timezoneOverride)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var timezoneFromConfig = root.TryGetProperty("timezone", out var timezoneEl)
            ? timezoneEl.GetString()
            : null;
        var timezoneName = !string.IsNullOrWhiteSpace(timezoneOverride)
            ? timezoneOverride
            : (string.IsNullOrWhiteSpace(timezoneFromConfig) ? "America/Los_Angeles" : timezoneFromConfig);

        var timezone = ResolveTimeZone(timezoneName);
        var defaultWindow = ReadInt(root, "defaultWindowMinutes", 120);
        var graceMinutes = ReadInt(root, "graceMinutes", 15);
        var endingSoonMinutes = ReadInt(root, "endingSoonMinutes", 10);
        var dayWindows = ParseDayWindows(root);

        if (!root.TryGetProperty("users", out var usersEl) || usersEl.ValueKind != JsonValueKind.Array)
        {
            throw new AppHttpException(500, "Конфигурация должна содержать массив users");
        }

        var users = new List<UserLimitConfig>();
        var normalizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledGradePolicyCount = 0;
        foreach (var userEl in usersEl.EnumerateArray())
        {
            var name = userEl.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? string.Empty).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new AppHttpException(500, "У каждого пользователя в конфиге должен быть name");
            }

            if (!normalizedNames.Add(name))
            {
                throw new AppHttpException(500, $"Имена пользователей должны быть уникальны без учета регистра и внешних пробелов: {name}");
            }

            var displayName = userEl.TryGetProperty("displayName", out var displayEl)
                ? (displayEl.GetString() ?? name).Trim()
                : name;
            var email = ReadEmail(userEl, "email");
            var parentEmails = ReadEmailList(userEl, "parentEmail");

            var userDefaultWindow = ReadInt(userEl, "defaultWindowMinutes", defaultWindow);

            var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (userEl.TryGetProperty("limitsMinutes", out var limitsEl) && limitsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var day in DayKeys)
                {
                    limits[day] = ReadInt(limitsEl, day, 0);
                }
            }
            else
            {
                foreach (var day in DayKeys)
                {
                    limits[day] = 0;
                }
            }

            var gradeLimitPolicy = ParseGradeLimitPolicy(userEl, name);
            if (gradeLimitPolicy?.Enabled == true)
            {
                enabledGradePolicyCount++;
            }

            users.Add(new UserLimitConfig(name, displayName, email, parentEmails, userDefaultWindow, limits, gradeLimitPolicy));
        }

        if (enabledGradePolicyCount > 1)
        {
            throw new AppHttpException(500, "В текущей версии gradeLimitPolicy может быть включен только для одного пользователя");
        }

        return new AccessConfig(timezone, defaultWindow, graceMinutes, endingSoonMinutes, dayWindows, users);
    }

    private static GradeLimitPolicyConfig? ParseGradeLimitPolicy(JsonElement userEl, string userName)
    {
        if (!userEl.TryGetProperty("gradeLimitPolicy", out var policyEl))
        {
            return null;
        }

        if (policyEl.ValueKind != JsonValueKind.Object)
        {
            throw new AppHttpException(500, $"gradeLimitPolicy для {userName} должен быть JSON-объектом");
        }

        var enabled = ReadOptionalBoolean(policyEl, "enabled", false, $"gradeLimitPolicy для {userName}");
        if (!enabled)
        {
            return new GradeLimitPolicyConfig(false, 0.85, 0, 20_160);
        }

        var threshold = ReadRequiredDouble(policyEl, "threshold", $"gradeLimitPolicy для {userName}");
        if (!double.IsFinite(threshold) || threshold < 0 || threshold > 1)
        {
            throw new AppHttpException(500, $"gradeLimitPolicy.threshold для {userName} должен быть в диапазоне от 0 до 1");
        }

        var restrictedLimitMinutes = ReadRequiredNonNegativeInt(
            policyEl,
            "restrictedLimitMinutes",
            $"gradeLimitPolicy для {userName}");
        var normalDecisionTtlMinutes = ReadRequiredPositiveInt(
            policyEl,
            "normalDecisionTtlMinutes",
            $"gradeLimitPolicy для {userName}");

        return new GradeLimitPolicyConfig(true, threshold, restrictedLimitMinutes, normalDecisionTtlMinutes);
    }

    private static bool ReadOptionalBoolean(JsonElement obj, string property, bool fallback, string context)
    {
        if (!obj.TryGetProperty(property, out var el))
        {
            return fallback;
        }

        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return el.GetBoolean();
        }

        throw new AppHttpException(500, $"{context}.{property} должен быть true или false");
    }

    private static double ReadRequiredDouble(JsonElement obj, string property, string context)
    {
        if (!obj.TryGetProperty(property, out var el)
            || el.ValueKind != JsonValueKind.Number
            || !el.TryGetDouble(out var value))
        {
            throw new AppHttpException(500, $"{context}.{property} должен быть числом");
        }

        return value;
    }

    private static int ReadRequiredNonNegativeInt(JsonElement obj, string property, string context)
    {
        if (!obj.TryGetProperty(property, out var el)
            || el.ValueKind != JsonValueKind.Number
            || !el.TryGetInt32(out var value)
            || value < 0)
        {
            throw new AppHttpException(500, $"{context}.{property} должен быть целым неотрицательным числом");
        }

        return value;
    }

    private static int ReadRequiredPositiveInt(JsonElement obj, string property, string context)
    {
        var value = ReadRequiredNonNegativeInt(obj, property, context);
        if (value == 0)
        {
            throw new AppHttpException(500, $"{context}.{property} должен быть больше 0");
        }

        return value;
    }

    private static Dictionary<string, DayWindowConfig> ParseDayWindows(JsonElement root)
    {
        var result = new Dictionary<string, DayWindowConfig>(StringComparer.OrdinalIgnoreCase);
        var hasConfig = root.TryGetProperty("dayWindows", out var windowsEl) && windowsEl.ValueKind == JsonValueKind.Object;

        foreach (var day in DayKeys)
        {
            var startMinutes = 0;
            var endMinutes = (24 * 60) - 1;

            if (hasConfig && windowsEl.TryGetProperty(day, out var dayEl) && dayEl.ValueKind == JsonValueKind.Object)
            {
                var startRaw = dayEl.TryGetProperty("start", out var startEl) ? startEl.GetString() : null;
                var endRaw = dayEl.TryGetProperty("end", out var endEl) ? endEl.GetString() : null;

                startMinutes = ParseTimeOfDayOrDefault(startRaw, 0, $"dayWindows.{day}.start");
                endMinutes = ParseTimeOfDayOrDefault(endRaw, (24 * 60) - 1, $"dayWindows.{day}.end");
            }

            if (endMinutes <= startMinutes)
            {
                throw new AppHttpException(500, $"Некорректное окно дня: dayWindows.{day}.end должно быть позже start");
            }

            result[day] = new DayWindowConfig(startMinutes, endMinutes);
        }

        return result;
    }

    private static int ReadInt(JsonElement obj, string property, int fallback)
    {
        if (!obj.TryGetProperty(property, out var el))
        {
            return Math.Max(0, fallback);
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var v) => Math.Max(0, v),
            JsonValueKind.String when int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => Math.Max(0, s),
            _ => Math.Max(0, fallback)
        };
    }

    private static string? ReadEmail(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = (el.GetString() ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static List<string> ReadEmailList(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el))
        {
            return [];
        }

        var result = new List<string>();
        if (el.ValueKind == JsonValueKind.String)
        {
            var value = (el.GetString() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }

            return result;
        }

        if (el.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = (item.GetString() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ParseTimeOfDayOrDefault(string? raw, int fallbackMinutes, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallbackMinutes;
        }

        var value = raw.Trim();
        var normalized = value.Replace('.', ':').Replace('-', ':');
        var parts = normalized.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new AppHttpException(500, $"Некорректный формат времени в {fieldName}: {raw}");
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            throw new AppHttpException(500, $"Некорректный формат времени в {fieldName}: {raw}");
        }

        if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
        {
            throw new AppHttpException(500, $"Время вне диапазона в {fieldName}: {raw}");
        }

        return (hour * 60) + minute;
    }

    private static TimeZoneInfo ResolveTimeZone(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        }

        var trimmed = zone.Trim();
        var candidates = new List<string> { trimmed };

        if (string.Equals(trimmed, "LA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "Los_Angeles", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("America/Los_Angeles");
            candidates.Add("Pacific Standard Time");
        }
        else if (string.Equals(trimmed, "America/Los_Angeles", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("Pacific Standard Time");
        }
        else if (string.Equals(trimmed, "Pacific Standard Time", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("America/Los_Angeles");
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch
            {
            }
        }

        return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    }
}
