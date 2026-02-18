using System.Globalization;
using System.Text;

sealed class RuntimeSettings
{
    public required string MikrotikKidControlUrl { get; init; }
    public required string AuthorizationHeader { get; init; }
    public required string DbPath { get; init; }
    public required string LimitsConfigPath { get; init; }
    public required int ConfigCacheTtlMs { get; init; }
    public required int SweepIntervalSeconds { get; init; }
    public required string TimezoneOverride { get; init; }
    public required string SmtpHost { get; init; }
    public required int SmtpPort { get; init; }
    public required string SmtpUser { get; init; }
    public required string SmtpPassword { get; init; }
    public required string SmtpFrom { get; init; }
    public required bool SmtpUseSsl { get; init; }

    public static RuntimeSettings Load(IConfiguration cfg)
    {
        var apiUrl = cfg["MIKROTIK_KID_CONTROL_URL"];
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            var baseUrl = cfg["MIKROTIK_BASE_URL"] ?? "http://192.168.88.254";
            apiUrl = $"{baseUrl.TrimEnd('/')}/rest/ip/kid-control";
        }

        var authHeader = cfg["BASIC_AUTH"];
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            var user = cfg["MIKROTIK_USER"];
            var pass = cfg["MIKROTIK_PASSWORD"];
            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
                authHeader = $"Basic {b64}";
            }
        }

        return new RuntimeSettings
        {
            MikrotikKidControlUrl = apiUrl,
            AuthorizationHeader = authHeader ?? string.Empty,
            DbPath = cfg["DB_PATH"] ?? "/data/kid-control-state.db",
            LimitsConfigPath = cfg["LIMITS_CONFIG_PATH"] ?? "/app/config/kid-access-config.json",
            ConfigCacheTtlMs = ParseInt(cfg["CONFIG_CACHE_TTL_MS"], 5000),
            SweepIntervalSeconds = ParseInt(cfg["SWEEP_INTERVAL_SECONDS"], 10),
            TimezoneOverride = cfg["APP_TIMEZONE"] ?? cfg["TIMEZONE"] ?? string.Empty,
            SmtpHost = (cfg["SMTP_HOST"] ?? string.Empty).Trim(),
            SmtpPort = ParseInt(cfg["SMTP_PORT"], 587),
            SmtpUser = (cfg["SMTP_USER"] ?? string.Empty).Trim(),
            SmtpPassword = cfg["SMTP_PASSWORD"] ?? string.Empty,
            SmtpFrom = (cfg["SMTP_FROM"] ?? string.Empty).Trim(),
            SmtpUseSsl = ParseBool(cfg["SMTP_USE_SSL"], true)
        };
    }

    private static int ParseInt(string? value, int fallback)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(1, parsed);
        }

        return fallback;
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        if (string.Equals(value, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(value, "0", StringComparison.Ordinal))
        {
            return false;
        }

        return fallback;
    }
}
