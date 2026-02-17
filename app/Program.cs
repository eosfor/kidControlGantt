using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

var runtime = RuntimeSettings.Load(builder.Configuration);
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton<AccessConfigProvider>();
builder.Services.AddSingleton<SessionRepository>();
builder.Services.AddHttpClient<MikrotikClient>((sp, client) =>
{
    var settings = sp.GetRequiredService<RuntimeSettings>();
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    if (!string.IsNullOrWhiteSpace(settings.AuthorizationHeader))
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", settings.AuthorizationHeader);
    }
});
builder.Services.AddSingleton<KidControlService>();
builder.Services.AddHostedService<SessionSweepHostedService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Text("OK"));

app.MapGet("/api/kid-control", async (KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.GetKidControlAsync(ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapGet("/api/state", async (KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.GetStateAsync(ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapPost("/api/users/{name}/request", async (string name, RequestWindowDto body, KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.RequestAccessAsync(name, body.WindowMinutes, ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapPost("/api/users/{name}/disable", async (string name, KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.DisableAccessAsync(name, ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapGet("/api/debug", async (KidControlService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.GetDebugAsync(ct);
        return Results.Ok(result);
    }
    catch (AppHttpException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

app.MapFallbackToFile("index.html");

app.Run();

sealed class SessionSweepHostedService : BackgroundService
{
    private readonly KidControlService _service;
    private readonly RuntimeSettings _settings;
    private readonly ILogger<SessionSweepHostedService> _logger;

    public SessionSweepHostedService(KidControlService service, RuntimeSettings settings, ILogger<SessionSweepHostedService> logger)
    {
        _service = service;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _service.SweepExpiredSessions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session sweep failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.SweepIntervalSeconds), stoppingToken);
        }
    }
}

sealed class KidControlService
{
    private static readonly string[] DayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly Dictionary<string, string> DayLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = "Понедельник",
        ["tue"] = "Вторник",
        ["wed"] = "Среда",
        ["thu"] = "Четверг",
        ["fri"] = "Пятница",
        ["sat"] = "Суббота",
        ["sun"] = "Воскресенье"
    };

    private readonly AccessConfigProvider _configProvider;
    private readonly SessionRepository _repo;
    private readonly MikrotikClient _mikrotik;

    public KidControlService(AccessConfigProvider configProvider, SessionRepository repo, MikrotikClient mikrotik)
    {
        _configProvider = configProvider;
        _repo = repo;
        _mikrotik = mikrotik;
    }

    public void SweepExpiredSessions()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expired = _repo.ListExpiredActiveSessions(nowMs);
        foreach (var session in expired)
        {
            _repo.EndSession(session.Id, session.ExpiresAtMs, "expired", nowMs);
        }
    }

    public async Task<List<Dictionary<string, string>>> GetKidControlAsync(CancellationToken ct)
    {
        return await _mikrotik.GetKidControlListAsync(ct);
    }

    public async Task<StateResponse> GetStateAsync(CancellationToken ct)
    {
        SweepExpiredSessions();
        var cfg = _configProvider.GetConfig();
        return await BuildStateAsync(cfg, ct);
    }

    public async Task<RequestResponse> RequestAccessAsync(string userName, int? windowMinutes, CancellationToken ct)
    {
        SweepExpiredSessions();
        var cfg = _configProvider.GetConfig();
        var userCfg = cfg.Users.FirstOrDefault(u => string.Equals(u.Name, userName, StringComparison.Ordinal));

        if (userCfg is null)
        {
            throw new AppHttpException(404, $"Пользователь {userName} отсутствует в конфигурации");
        }

        var day = GetDayContext(cfg.TimeZone);
        var dayLimitMinutes = userCfg.LimitsMinutes.GetValueOrDefault(day.DayKey, 0);
        var requestedWindowMinutes = Math.Clamp(windowMinutes ?? userCfg.DefaultWindowMinutes, 0, dayLimitMinutes);

        if (requestedWindowMinutes <= 0)
        {
            throw new AppHttpException(400, "Размер окна должен быть больше 0 минут");
        }

        var usage = GetUsageState(userCfg.Name, day.StartOfDayMs, day.NextDayMs, day.NowMs);
        var dayLimitSeconds = dayLimitMinutes * 60;

        if (usage.UsedSeconds >= dayLimitSeconds)
        {
            throw new AppHttpException(409, $"Дневной лимит ({dayLimitMinutes} мин) исчерпан");
        }

        var maxPerDaySeconds = dayLimitSeconds + (cfg.GraceMinutes * 60);
        var remainingCapSeconds = Math.Max(0, maxPerDaySeconds - usage.UsedSeconds);
        var secondsUntilEndOfDay = Math.Max(0, (int)((day.NextDayMs - day.NowMs) / 1000));
        var maxGrantSecondsNow = Math.Min(remainingCapSeconds, secondsUntilEndOfDay);

        if (maxGrantSecondsNow <= 0)
        {
            throw new AppHttpException(409, "Невозможно выдать доступ: достигнут верхний лимит или завершился день");
        }

        var entries = await _mikrotik.GetKidControlListAsync(ct);
        var entry = entries.FirstOrDefault(e => string.Equals(e.GetValueOrDefault("name"), userCfg.Name, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new AppHttpException(404, $"Пользователь {userCfg.Name} не найден в MikroTik Kid Control");
        }

        var requestedWindowSeconds = requestedWindowMinutes * 60;
        var active = usage.ActiveSession;
        var startMs = active is null ? day.NowMs : active.StartedAtMs;
        var extensionBaseMs = active is null ? day.NowMs : Math.Max(active.ExpiresAtMs, day.NowMs);

        var targetEndMs = extensionBaseMs + (requestedWindowSeconds * 1000L);
        var maxAllowedEndMs = day.NowMs + (maxGrantSecondsNow * 1000L);
        if (targetEndMs > maxAllowedEndMs)
        {
            targetEndMs = maxAllowedEndMs;
        }

        if (targetEndMs <= day.NowMs)
        {
            throw new AppHttpException(409, "Недостаточно остатка времени для продления доступа");
        }

        await _mikrotik.ApplyWindowAsync(entry, day.DayKey, startMs, targetEndMs, cfg.TimeZone, ct);

        if (active is null)
        {
            _repo.InsertSession(userCfg.Name, day.NowMs, targetEndMs, day.NowMs);
        }
        else
        {
            _repo.ExtendSession(active.Id, targetEndMs, day.NowMs);
        }

        var state = await BuildStateAsync(cfg, ct);
        return new RequestResponse(true, userCfg.Name, requestedWindowMinutes, targetEndMs, state);
    }

    public async Task<DisableResponse> DisableAccessAsync(string userName, CancellationToken ct)
    {
        SweepExpiredSessions();
        var cfg = _configProvider.GetConfig();
        var userCfg = cfg.Users.FirstOrDefault(u => string.Equals(u.Name, userName, StringComparison.Ordinal));

        if (userCfg is null)
        {
            throw new AppHttpException(404, $"Пользователь {userName} отсутствует в конфигурации");
        }

        var day = GetDayContext(cfg.TimeZone);

        var entries = await _mikrotik.GetKidControlListAsync(ct);
        var entry = entries.FirstOrDefault(e => string.Equals(e.GetValueOrDefault("name"), userCfg.Name, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new AppHttpException(404, $"Пользователь {userCfg.Name} не найден в MikroTik Kid Control");
        }

        var active = _repo.GetActiveSession(userCfg.Name);
        if (active is not null)
        {
            _repo.EndSession(active.Id, day.NowMs, "manual", day.NowMs);
        }

        await _mikrotik.DisableUserAsync(entry, day.DayKey, ct);

        var state = await BuildStateAsync(cfg, ct);
        return new DisableResponse(true, userCfg.Name, state);
    }

    public async Task<object> GetDebugAsync(CancellationToken ct)
    {
        var cfg = _configProvider.GetConfig();
        var list = await _mikrotik.GetKidControlListAsync(ct);
        return new
        {
            upstreamUrl = _mikrotik.ApiUrl,
            authConfigured = !string.IsNullOrWhiteSpace(_mikrotik.AuthorizationHeader),
            timezone = cfg.TimeZone.Id,
            usersInConfig = cfg.Users.Count,
            upstreamEntries = list.Count
        };
    }

    private async Task<StateResponse> BuildStateAsync(AccessConfig cfg, CancellationToken ct)
    {
        var day = GetDayContext(cfg.TimeZone);
        var entries = await _mikrotik.GetKidControlListAsync(ct);
        var map = entries.ToDictionary(k => k.GetValueOrDefault("name") ?? string.Empty, v => v, StringComparer.Ordinal);

        var users = new List<UserStateRow>();

        foreach (var userCfg in cfg.Users)
        {
            map.TryGetValue(userCfg.Name, out var entry);
            users.Add(BuildStateRow(cfg, userCfg, entry, day));
        }

        return new StateResponse(
            day.NowMs,
            cfg.TimeZone.Id,
            day.DayKey,
            DayLabels[day.DayKey],
            cfg.DefaultWindowMinutes,
            cfg.GraceMinutes,
            users
        );
    }

    private UserStateRow BuildStateRow(AccessConfig cfg, UserLimitConfig userCfg, Dictionary<string, string>? mikrotikEntry, DayContext day)
    {
        var dayLimitMinutes = userCfg.LimitsMinutes.GetValueOrDefault(day.DayKey, 0);
        var dayLimitSeconds = dayLimitMinutes * 60;
        var maxPerDaySeconds = dayLimitSeconds + (cfg.GraceMinutes * 60);

        var usage = GetUsageState(userCfg.Name, day.StartOfDayMs, day.NextDayMs, day.NowMs);

        var remainingSeconds = Math.Max(0, dayLimitSeconds - usage.UsedSeconds);
        var remainingCapSeconds = Math.Max(0, maxPerDaySeconds - usage.UsedSeconds);
        var secondsUntilEndOfDay = Math.Max(0, (int)((day.NextDayMs - day.NowMs) / 1000));
        var maxGrantSecondsNow = Math.Max(0, Math.Min(remainingCapSeconds, secondsUntilEndOfDay));

        var existsInMikrotik = mikrotikEntry is not null;
        var mikrotikDisabled = ParseBool(mikrotikEntry?.GetValueOrDefault("disabled") ?? "true");

        ActiveSessionRow? activeSession = null;
        if (usage.ActiveSession is not null)
        {
            var session = usage.ActiveSession;
            activeSession = new ActiveSessionRow(
                session.Id,
                session.StartedAtMs,
                session.ExpiresAtMs,
                Math.Max(0, (int)((session.ExpiresAtMs - day.NowMs) / 1000)),
                Math.Max(0, (int)((session.ExpiresAtMs - session.StartedAtMs) / 1000))
            );
        }

        return new UserStateRow(
            userCfg.Name,
            userCfg.DisplayName,
            existsInMikrotik,
            mikrotikEntry?.GetValueOrDefault(".id"),
            mikrotikDisabled,
            dayLimitMinutes,
            usage.UsedSeconds,
            remainingSeconds,
            remainingCapSeconds,
            maxGrantSecondsNow,
            existsInMikrotik && usage.UsedSeconds < dayLimitSeconds && maxGrantSecondsNow > 0,
            Math.Clamp(userCfg.DefaultWindowMinutes, 0, dayLimitMinutes),
            activeSession
        );
    }

    private UsageState GetUsageState(string userName, long startOfDayMs, long nextDayMs, long nowMs)
    {
        var completedMs = _repo.SumCompletedUsageMs(userName, startOfDayMs, nextDayMs);
        var active = _repo.GetActiveSession(userName);

        long activeMs = 0;
        if (active is not null)
        {
            var activeStart = Math.Max(active.StartedAtMs, startOfDayMs);
            var activeEnd = Math.Min(Math.Min(nowMs, active.ExpiresAtMs), nextDayMs);
            activeMs = Math.Max(0, activeEnd - activeStart);
        }

        var usedSeconds = (int)((completedMs + activeMs) / 1000);
        return new UsageState(usedSeconds, active);
    }

    private static bool ParseBool(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static DayContext GetDayContext(TimeZoneInfo zone)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var dayKey = localNow.DayOfWeek switch
        {
            DayOfWeek.Monday => "mon",
            DayOfWeek.Tuesday => "tue",
            DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu",
            DayOfWeek.Friday => "fri",
            DayOfWeek.Saturday => "sat",
            DayOfWeek.Sunday => "sun",
            _ => "mon"
        };

        var startLocal = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var nextLocal = startLocal.AddDays(1);

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, zone);
        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocal, zone);

        return new DayContext(
            nowUtc.ToUnixTimeMilliseconds(),
            dayKey,
            new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            new DateTimeOffset(nextUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()
        );
    }

    private sealed record DayContext(long NowMs, string DayKey, long StartOfDayMs, long NextDayMs);
    private sealed record UsageState(int UsedSeconds, SessionRecord? ActiveSession);
}

sealed class MikrotikClient
{
    private readonly HttpClient _http;

    public MikrotikClient(HttpClient http, RuntimeSettings settings)
    {
        _http = http;
        ApiUrl = settings.MikrotikKidControlUrl;
        AuthorizationHeader = settings.AuthorizationHeader;
    }

    public string ApiUrl { get; }
    public string AuthorizationHeader { get; }

    public async Task<List<Dictionary<string, string>>> GetKidControlListAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            throw new AppHttpException(502, $"MikroTik GET {ApiUrl} => {(int)res.StatusCode}. {body}".Trim());
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new AppHttpException(502, "MikroTik вернул неожиданный формат данных для /ip/kid-control");
            }

            var list = new List<Dictionary<string, string>>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in item.EnumerateObject())
                {
                    row[prop.Name] = ToSimpleString(prop.Value);
                }
                list.Add(row);
            }

            return list;
        }
        catch (JsonException ex)
        {
            throw new AppHttpException(502, $"Некорректный JSON от MikroTik: {ex.Message}");
        }
    }

    public async Task ApplyWindowAsync(Dictionary<string, string> entry, string dayKey, long startMs, long endMs, TimeZoneInfo zone, CancellationToken ct)
    {
        var value = BuildWindowValue(startMs, endMs, zone);
        var patch = new Dictionary<string, string>
        {
            [dayKey] = value,
            ["disabled"] = "false"
        };

        await PatchEntryAsync(entry, patch, ct);
    }

    public async Task DisableUserAsync(Dictionary<string, string> entry, string dayKey, CancellationToken ct)
    {
        var patch = new Dictionary<string, string>
        {
            [dayKey] = string.Empty,
            ["disabled"] = "true"
        };

        await PatchEntryAsync(entry, patch, ct);
    }

    private async Task PatchEntryAsync(Dictionary<string, string> entry, Dictionary<string, string> patch, CancellationToken ct)
    {
        if (!entry.TryGetValue(".id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            throw new AppHttpException(502, "MikroTik entry не содержит .id");
        }

        var url = $"{ApiUrl.TrimEnd('/')}/{id}";

        using var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json")
        };

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            throw new AppHttpException(502, $"MikroTik PATCH {url} => {(int)res.StatusCode}. {body}".Trim());
        }
    }

    private static string BuildWindowValue(long startMs, long endMs, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(startMs), zone);
        var end = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(endMs), zone);
        return $"{ToMikrotikTime(start)}-{ToMikrotikTime(end)}";
    }

    private static string ToMikrotikTime(DateTimeOffset dt)
    {
        if (dt.Minute == 0)
        {
            return $"{dt.Hour}h";
        }

        return $"{dt.Hour}h{dt.Minute}m";
    }

    private static string ToSimpleString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => element.GetRawText()
        };
    }
}

sealed class SessionRepository
{
    private readonly string _connectionString;
    private readonly object _sync = new();

    public SessionRepository(RuntimeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DbPath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = settings.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    public SessionRecord? GetActiveSession(string userName)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, user_name, started_at_ms, expires_at_ms
                FROM sessions
                WHERE user_name = $user_name AND ended_at_ms IS NULL
                LIMIT 1";
            cmd.Parameters.AddWithValue("$user_name", userName);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new SessionRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3)
            );
        }
    }

    public List<SessionRecord> ListExpiredActiveSessions(long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, user_name, started_at_ms, expires_at_ms
                FROM sessions
                WHERE ended_at_ms IS NULL AND expires_at_ms <= $now_ms
                ORDER BY expires_at_ms ASC";
            cmd.Parameters.AddWithValue("$now_ms", nowMs);

            using var reader = cmd.ExecuteReader();
            var result = new List<SessionRecord>();
            while (reader.Read())
            {
                result.Add(new SessionRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)
                ));
            }

            return result;
        }
    }

    public long SumCompletedUsageMs(string userName, long startMs, long endMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(SUM(ended_at_ms - started_at_ms), 0)
                FROM sessions
                WHERE user_name = $user_name
                  AND ended_at_ms IS NOT NULL
                  AND started_at_ms >= $start_ms
                  AND started_at_ms < $end_ms";
            cmd.Parameters.AddWithValue("$user_name", userName);
            cmd.Parameters.AddWithValue("$start_ms", startMs);
            cmd.Parameters.AddWithValue("$end_ms", endMs);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }
    }

    public void InsertSession(string userName, long startedAtMs, long expiresAtMs, long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO sessions (user_name, started_at_ms, expires_at_ms, ended_at_ms, ended_reason, created_at_ms, updated_at_ms)
                VALUES ($user_name, $started_at_ms, $expires_at_ms, NULL, NULL, $now_ms, $now_ms)";
            cmd.Parameters.AddWithValue("$user_name", userName);
            cmd.Parameters.AddWithValue("$started_at_ms", startedAtMs);
            cmd.Parameters.AddWithValue("$expires_at_ms", expiresAtMs);
            cmd.Parameters.AddWithValue("$now_ms", nowMs);
            cmd.ExecuteNonQuery();
        }
    }

    public void ExtendSession(long id, long expiresAtMs, long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE sessions
                SET expires_at_ms = $expires_at_ms, updated_at_ms = $now_ms
                WHERE id = $id AND ended_at_ms IS NULL";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$expires_at_ms", expiresAtMs);
            cmd.Parameters.AddWithValue("$now_ms", nowMs);
            cmd.ExecuteNonQuery();
        }
    }

    public void EndSession(long id, long endedAtMs, string reason, long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE sessions
                SET ended_at_ms = $ended_at_ms, ended_reason = $reason, updated_at_ms = $now_ms
                WHERE id = $id AND ended_at_ms IS NULL";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ended_at_ms", endedAtMs);
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$now_ms", nowMs);
            cmd.ExecuteNonQuery();
        }
    }

    private void Initialize()
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sessions (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  user_name TEXT NOT NULL,
                  started_at_ms INTEGER NOT NULL,
                  expires_at_ms INTEGER NOT NULL,
                  ended_at_ms INTEGER,
                  ended_reason TEXT,
                  created_at_ms INTEGER NOT NULL,
                  updated_at_ms INTEGER NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_sessions_active_user
                  ON sessions (user_name)
                  WHERE ended_at_ms IS NULL;

                CREATE INDEX IF NOT EXISTS idx_sessions_user_start
                  ON sessions (user_name, started_at_ms);
            ";
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}

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
            var fresh = _cached is not null
                && currentMtime == _mtimeMs
                && (now - _loadedAt).TotalMilliseconds < _settings.ConfigCacheTtlMs;

            if (fresh)
            {
                return _cached!;
            }

            var raw = File.ReadAllText(_settings.LimitsConfigPath);
            _cached = ParseConfig(raw);
            _mtimeMs = currentMtime;
            _loadedAt = now;
            return _cached;
        }
    }

    private static AccessConfig ParseConfig(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var timezoneName = root.TryGetProperty("timezone", out var timezoneEl)
            ? timezoneEl.GetString() ?? "UTC"
            : "UTC";

        var timezone = ResolveTimeZone(timezoneName);
        var defaultWindow = ReadInt(root, "defaultWindowMinutes", 120);
        var graceMinutes = ReadInt(root, "graceMinutes", 15);

        if (!root.TryGetProperty("users", out var usersEl) || usersEl.ValueKind != JsonValueKind.Array)
        {
            throw new AppHttpException(500, "Конфигурация должна содержать массив users");
        }

        var users = new List<UserLimitConfig>();
        foreach (var userEl in usersEl.EnumerateArray())
        {
            var name = userEl.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? string.Empty).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new AppHttpException(500, "У каждого пользователя в конфиге должен быть name");
            }

            var displayName = userEl.TryGetProperty("displayName", out var displayEl)
                ? (displayEl.GetString() ?? name).Trim()
                : name;

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

            users.Add(new UserLimitConfig(name, displayName, userDefaultWindow, limits));
        }

        return new AccessConfig(timezone, defaultWindow, graceMinutes, users);
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

    private static TimeZoneInfo ResolveTimeZone(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zone.Trim());
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }
}

sealed class RuntimeSettings
{
    public required string MikrotikKidControlUrl { get; init; }
    public required string AuthorizationHeader { get; init; }
    public required string DbPath { get; init; }
    public required string LimitsConfigPath { get; init; }
    public required int ConfigCacheTtlMs { get; init; }
    public required int SweepIntervalSeconds { get; init; }

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
            SweepIntervalSeconds = ParseInt(cfg["SWEEP_INTERVAL_SECONDS"], 10)
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
}

sealed class AppHttpException : Exception
{
    public AppHttpException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

sealed record AccessConfig(TimeZoneInfo TimeZone, int DefaultWindowMinutes, int GraceMinutes, List<UserLimitConfig> Users);
sealed record UserLimitConfig(string Name, string DisplayName, int DefaultWindowMinutes, Dictionary<string, int> LimitsMinutes);
sealed record SessionRecord(long Id, string UserName, long StartedAtMs, long ExpiresAtMs);
sealed record ActiveSessionRow(long Id, long StartedAtMs, long EndsAtMs, int RemainingSeconds, int TotalSeconds);
sealed record UserStateRow(
    string Name,
    string DisplayName,
    bool ExistsInMikrotik,
    string? MikrotikId,
    bool MikrotikDisabled,
    int DayLimitMinutes,
    int UsedSeconds,
    int RemainingSeconds,
    int RemainingCapSeconds,
    int MaxGrantSecondsNow,
    bool CanRequest,
    int SuggestedWindowMinutes,
    ActiveSessionRow? ActiveSession
);

sealed record StateResponse(
    long ServerTimeMs,
    string Timezone,
    string DayKey,
    string DayLabel,
    int DefaultWindowMinutes,
    int GraceMinutes,
    List<UserStateRow> Users
);

sealed record RequestWindowDto(int? WindowMinutes);
sealed record RequestResponse(bool Ok, string User, int RequestedWindowMinutes, long GrantedUntilMs, StateResponse State);
sealed record DisableResponse(bool Ok, string User, StateResponse State);
