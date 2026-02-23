using System.Globalization;

sealed class KidControlService
{
    private const int DayEndCutoffGraceMinutes = 10;

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
    private readonly EmailNotificationService _email;
    private readonly ILogger<KidControlService> _logger;

    public KidControlService(
        AccessConfigProvider configProvider,
        SessionRepository repo,
        MikrotikClient mikrotik,
        EmailNotificationService email,
        ILogger<KidControlService> logger)
    {
        _configProvider = configProvider;
        _repo = repo;
        _mikrotik = mikrotik;
        _email = email;
        _logger = logger;
    }

    public void SweepExpiredSessions()
    {
        SweepExpiredSessionsAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task SweepExpiredSessionsAsync(CancellationToken ct)
    {
        var cfg = _configProvider.GetConfig();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expired = _repo.ListExpiredActiveSessions(nowMs);
        Dictionary<string, Dictionary<string, string>>? entriesByUser = null;

        if (expired.Count > 0)
        {
            try
            {
                var entries = await _mikrotik.GetKidControlListAsync(ct);
                entriesByUser = entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.GetValueOrDefault("name")))
                    .GroupBy(e => e.GetValueOrDefault("name") ?? string.Empty, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load MikroTik Kid Control list during expired-session sweep; will retry later");
            }
        }

        foreach (var session in expired)
        {
            if (entriesByUser is null)
            {
                // Don't mark the session expired in DB until MikroTik window/pause cleanup succeeds.
                continue;
            }

            try
            {
                if (entriesByUser.TryGetValue(session.UserName, out var entry))
                {
                    var dayKey = GetDayKeyForTimestamp(session.ExpiresAtMs, cfg.TimeZone);
                    await _mikrotik.DisableUserAsync(entry, dayKey, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "MikroTik entry for expired session user {UserName} not found during sweep; ending session in local DB only",
                        session.UserName);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to clear MikroTik window/pause user {UserName} for expired session {SessionId}; will retry later",
                    session.UserName,
                    session.Id);
                continue;
            }

            var ended = _repo.EndSession(session.Id, session.ExpiresAtMs, "expired", nowMs);
            if (ended)
            {
                var grantedWindowMinutes = (int)Math.Max(0, (session.ExpiresAtMs - session.StartedAtMs) / 60_000L);
                _repo.InsertAuditEvent(
                    session.UserName,
                    "expired",
                    null,
                    grantedWindowMinutes,
                    session.ExpiresAtMs,
                    null,
                    session.ExpiresAtMs);

                var userCfg = cfg.Users.FirstOrDefault(u => string.Equals(u.Name, session.UserName, StringComparison.Ordinal));
                if (userCfg is not null)
                {
                    NotifyParentsExpired(cfg, userCfg, session.ExpiresAtMs);
                }
            }
        }

        NotifySoonEndingSessions(cfg, nowMs);
    }

    public async Task<List<Dictionary<string, string>>> GetKidControlAsync(CancellationToken ct)
    {
        return await _mikrotik.GetKidControlListAsync(ct);
    }

    public async Task<StateResponse> GetStateAsync(CancellationToken ct)
    {
        await SweepExpiredSessionsAsync(ct);
        var cfg = _configProvider.GetConfig();
        return await BuildStateAsync(cfg, ct);
    }

    public async Task<UserStatsResponse> GetUserStatsAsync(string userName, int days, CancellationToken ct)
    {
        await SweepExpiredSessionsAsync(ct);
        var cfg = _configProvider.GetConfig();
        var userCfg = cfg.Users.FirstOrDefault(u => string.Equals(u.Name, userName, StringComparison.Ordinal));

        if (userCfg is null)
        {
            throw new AppHttpException(404, $"Пользователь {userName} отсутствует в конфигурации");
        }

        var normalizedDays = Math.Clamp(days, 1, 30);
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, cfg.TimeZone);

        var todayStartLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var rangeStartLocal = todayStartLocal.AddDays(-(normalizedDays - 1));
        var rangeEndLocal = todayStartLocal.AddDays(1);

        var rangeStartUtcMs = ToUnixMilliseconds(cfg.TimeZone, rangeStartLocal);
        var rangeEndUtcMs = ToUnixMilliseconds(cfg.TimeZone, rangeEndLocal);

        var sessions = _repo.ListSessionsIntersectingRange(userCfg.Name, rangeStartUtcMs, rangeEndUtcMs);
        var segments = new List<UserStatsSegment>();

        foreach (var session in sessions)
        {
            var effectiveEndMs = session.EndedAtMs ?? session.ExpiresAtMs;
            if (effectiveEndMs <= session.StartedAtMs)
            {
                continue;
            }

            var clippedStartMs = Math.Max(session.StartedAtMs, rangeStartUtcMs);
            var clippedEndMs = Math.Min(effectiveEndMs, rangeEndUtcMs);
            if (clippedEndMs <= clippedStartMs)
            {
                continue;
            }

            var endReason = ResolveEndReason(session);
            segments.Add(new UserStatsSegment(
                session.Id,
                clippedStartMs,
                clippedEndMs,
                endReason,
                EndReasonLabel(endReason),
                Math.Max(0, effectiveEndMs - session.StartedAtMs),
                Math.Max(0, session.ExpiresAtMs - session.StartedAtMs)
            ));
        }

        var auditRows = _repo.ListAuditEventsIntersectingRange(userCfg.Name, rangeStartUtcMs, rangeEndUtcMs, 500);
        var auditEvents = BuildAuditEventsForStats(sessions, auditRows, rangeStartUtcMs, rangeEndUtcMs);

        var response = new UserStatsResponse(
            userCfg.Name,
            userCfg.DisplayName,
            cfg.TimeZone.Id,
            normalizedDays,
            nowUtc.ToUnixTimeMilliseconds(),
            rangeStartUtcMs,
            rangeEndUtcMs,
            segments,
            auditEvents
        );

        return response;
    }

    public async Task<RequestResponse> RequestAccessAsync(string userName, int? windowMinutes, CancellationToken ct)
    {
        await SweepExpiredSessionsAsync(ct);
        var cfg = _configProvider.GetConfig();
        var userCfg = cfg.Users.FirstOrDefault(u => string.Equals(u.Name, userName, StringComparison.Ordinal));

        if (userCfg is null)
        {
            throw new AppHttpException(404, $"Пользователь {userName} отсутствует в конфигурации");
        }

        var day = GetDayContext(cfg.TimeZone);
        var dayWindow = GetDayWindowBounds(cfg, day);
        if (day.NowMs < dayWindow.StartMs)
        {
            throw new AppHttpException(409, $"Запрос доступа возможен только после {FormatLocalTime(dayWindow.StartMs, cfg.TimeZone)}");
        }

        if (day.NowMs > dayWindow.EndMs)
        {
            throw new AppHttpException(409, $"Запрос доступа после {FormatLocalTime(dayWindow.EndMs, cfg.TimeZone)} недоступен");
        }

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
        var secondsUntilWindowCutoff = Math.Max(0, (int)((dayWindow.CutoffMs - day.NowMs) / 1000));
        // Cap grant by both daily allowance and the configured day-end cutoff.
        var maxGrantSecondsNow = Math.Min(remainingCapSeconds, secondsUntilWindowCutoff);

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
        var requestedEndMs = active is null
            ? day.NowMs + (requestedWindowSeconds * 1000L)
            : active.StartedAtMs + (requestedWindowSeconds * 1000L);
        if (active is not null && requestedEndMs <= day.NowMs)
        {
            throw new AppHttpException(409, $"Нельзя установить окно {requestedWindowMinutes} мин: это время уже прошло для текущей сессии");
        }

        var maxAllowedEndMs = day.NowMs + (maxGrantSecondsNow * 1000L);
        var targetEndMs = Math.Min(requestedEndMs, maxAllowedEndMs);

        if (targetEndMs <= day.NowMs)
        {
            throw new AppHttpException(409, "Недостаточно остатка времени для изменения доступа");
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

        var grantedWindowMinutes = (int)Math.Max(0, (targetEndMs - startMs) / 60_000L);
        var auditAction = active is null
            ? "request_started"
            : targetEndMs > active.ExpiresAtMs
                ? "window_extended"
                : targetEndMs < active.ExpiresAtMs
                    ? "window_shortened"
                    : "window_updated";
        var auditDetails = targetEndMs < requestedEndMs ? "window_clipped_by_limits" : null;
        _repo.InsertAuditEvent(
            userCfg.Name,
            auditAction,
            requestedWindowMinutes,
            grantedWindowMinutes,
            targetEndMs,
            auditDetails,
            day.NowMs);

        NotifyParentsAccessChanged(
            cfg,
            userCfg,
            auditAction,
            requestedWindowMinutes,
            grantedWindowMinutes,
            startMs,
            targetEndMs);

        var state = await BuildStateAsync(cfg, ct);
        return new RequestResponse(true, userCfg.Name, requestedWindowMinutes, targetEndMs, state);
    }

    public async Task<DisableResponse> DisableAccessAsync(string userName, CancellationToken ct)
    {
        await SweepExpiredSessionsAsync(ct);
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
        var hadActiveSession = false;
        if (active is not null)
        {
            hadActiveSession = _repo.EndSession(active.Id, day.NowMs, "manual", day.NowMs);
        }

        await _mikrotik.DisableUserAsync(entry, day.DayKey, ct);

        _repo.InsertAuditEvent(
            userCfg.Name,
            hadActiveSession ? "disable" : "disable_no_session",
            null,
            null,
            null,
            null,
            day.NowMs);

        NotifyParentsDisabled(cfg, userCfg, hadActiveSession, day.NowMs);

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
        var dayWindow = GetDayWindowBounds(cfg, day);
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
            dayWindow.StartMs,
            dayWindow.EndMs,
            dayWindow.CutoffMs,
            FormatLocalTime(dayWindow.StartMs, cfg.TimeZone),
            FormatLocalTime(dayWindow.EndMs, cfg.TimeZone),
            FormatLocalTime(dayWindow.CutoffMs, cfg.TimeZone),
            cfg.DefaultWindowMinutes,
            cfg.GraceMinutes,
            users
        );
    }

    private UserStateRow BuildStateRow(AccessConfig cfg, UserLimitConfig userCfg, Dictionary<string, string>? mikrotikEntry, DayContext day)
    {
        var dayWindow = GetDayWindowBounds(cfg, day);
        var dayLimitMinutes = userCfg.LimitsMinutes.GetValueOrDefault(day.DayKey, 0);
        var dayLimitSeconds = dayLimitMinutes * 60;
        var maxPerDaySeconds = dayLimitSeconds + (cfg.GraceMinutes * 60);

        var usage = GetUsageState(userCfg.Name, day.StartOfDayMs, day.NextDayMs, day.NowMs);

        var remainingSeconds = Math.Max(0, dayLimitSeconds - usage.UsedSeconds);
        var remainingCapSeconds = Math.Max(0, maxPerDaySeconds - usage.UsedSeconds);
        var secondsUntilWindowCutoff = Math.Max(0, (int)((dayWindow.CutoffMs - day.NowMs) / 1000));
        var maxGrantSecondsNow = Math.Max(0, Math.Min(remainingCapSeconds, secondsUntilWindowCutoff));
        var inRequestWindow = day.NowMs >= dayWindow.StartMs && day.NowMs <= dayWindow.EndMs;

        var existsInMikrotik = mikrotikEntry is not null;
        var mikrotikStatus = MikrotikStatusMapper.FromEntry(mikrotikEntry);

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
            mikrotikStatus.Disabled,
            mikrotikStatus.Paused,
            mikrotikStatus.Blocked,
            mikrotikStatus.Active,
            mikrotikStatus.Status,
            dayLimitMinutes,
            usage.UsedSeconds,
            remainingSeconds,
            remainingCapSeconds,
            maxGrantSecondsNow,
            existsInMikrotik && usage.UsedSeconds < dayLimitSeconds && maxGrantSecondsNow > 0 && inRequestWindow,
            Math.Clamp(userCfg.DefaultWindowMinutes, 0, dayLimitMinutes),
            activeSession
        );
    }

    private void NotifySoonEndingSessions(AccessConfig cfg, long nowMs)
    {
        if (cfg.EndingSoonMinutes <= 0)
        {
            return;
        }

        var deadlineMs = nowMs + (cfg.EndingSoonMinutes * 60_000L);
        var soonSessions = _repo.ListSoonEndingCandidates(nowMs, deadlineMs);
        foreach (var session in soonSessions)
        {
            var userCfg = cfg.Users.FirstOrDefault(u => string.Equals(u.Name, session.UserName, StringComparison.Ordinal));
            if (userCfg is null)
            {
                _repo.MarkSoonEndingNotified(session.Id, nowMs);
                continue;
            }

            var hasAnyRecipient = userCfg.ParentEmails.Count > 0 || !string.IsNullOrWhiteSpace(userCfg.Email);
            var remainingMinutes = Math.Max(1, (int)Math.Ceiling((session.ExpiresAtMs - nowMs) / 60_000d));

            var parentSent = NotifyParentsSoonEnding(cfg, userCfg, remainingMinutes, session.ExpiresAtMs);
            var childSent = NotifyChildSoonEnding(cfg, userCfg, remainingMinutes, session.ExpiresAtMs);
            // Mark as notified once delivery is not needed or at least one recipient received the message.
            if (!hasAnyRecipient || parentSent || childSent)
            {
                _repo.MarkSoonEndingNotified(session.Id, nowMs);
            }
        }
    }

    private void NotifyParentsAccessChanged(
        AccessConfig cfg,
        UserLimitConfig userCfg,
        string action,
        int requestedWindowMinutes,
        int grantedWindowMinutes,
        long startMs,
        long endMs)
    {
        if (userCfg.ParentEmails.Count == 0)
        {
            return;
        }

        var actionLabel = AuditActionLabel(action);
        var subject = $"KidControl: {userCfg.DisplayName} - {actionLabel}";
        var body = string.Join('\n', [
            $"Пользователь: {userCfg.DisplayName} ({userCfg.Name})",
            $"Событие: {actionLabel}",
            $"Запрошено: {requestedWindowMinutes} мин",
            $"Выдано: {grantedWindowMinutes} мин",
            $"Начало сессии: {FormatLocalDateTime(startMs, cfg.TimeZone)}",
            $"Окончание сессии: {FormatLocalDateTime(endMs, cfg.TimeZone)}",
            $"Таймзона: {cfg.TimeZone.Id}"
        ]);

        _email.Send(userCfg.ParentEmails, subject, body);
    }

    private void NotifyParentsDisabled(AccessConfig cfg, UserLimitConfig userCfg, bool hadActiveSession, long occurredAtMs)
    {
        if (userCfg.ParentEmails.Count == 0)
        {
            return;
        }

        var actionLabel = hadActiveSession ? "Отключено кнопкой" : "Отключение без активной сессии";
        var subject = $"KidControl: {userCfg.DisplayName} - {actionLabel}";
        var body = string.Join('\n', [
            $"Пользователь: {userCfg.DisplayName} ({userCfg.Name})",
            $"Событие: {actionLabel}",
            $"Время: {FormatLocalDateTime(occurredAtMs, cfg.TimeZone)}",
            $"Таймзона: {cfg.TimeZone.Id}"
        ]);

        _email.Send(userCfg.ParentEmails, subject, body);
    }

    private void NotifyParentsExpired(AccessConfig cfg, UserLimitConfig userCfg, long expiredAtMs)
    {
        if (userCfg.ParentEmails.Count == 0)
        {
            return;
        }

        var subject = $"KidControl: {userCfg.DisplayName} - Время доступа истекло";
        var body = string.Join('\n', [
            $"Пользователь: {userCfg.DisplayName} ({userCfg.Name})",
            $"Событие: Время доступа истекло автоматически",
            $"Время: {FormatLocalDateTime(expiredAtMs, cfg.TimeZone)}",
            $"Таймзона: {cfg.TimeZone.Id}"
        ]);

        _email.Send(userCfg.ParentEmails, subject, body);
    }

    private bool NotifyParentsSoonEnding(AccessConfig cfg, UserLimitConfig userCfg, int remainingMinutes, long expiresAtMs)
    {
        if (userCfg.ParentEmails.Count == 0)
        {
            return false;
        }

        var subject = $"KidControl: {userCfg.DisplayName} - Доступ скоро закончится";
        var body = string.Join('\n', [
            $"Пользователь: {userCfg.DisplayName} ({userCfg.Name})",
            $"Осталось: примерно {remainingMinutes} мин",
            $"Окончание: {FormatLocalDateTime(expiresAtMs, cfg.TimeZone)}",
            $"Таймзона: {cfg.TimeZone.Id}"
        ]);

        return _email.Send(userCfg.ParentEmails, subject, body);
    }

    private bool NotifyChildSoonEnding(AccessConfig cfg, UserLimitConfig userCfg, int remainingMinutes, long expiresAtMs)
    {
        if (string.IsNullOrWhiteSpace(userCfg.Email))
        {
            return false;
        }

        var subject = $"KidControl: {userCfg.DisplayName} - До отключения {remainingMinutes} мин";
        var body = string.Join('\n', [
            $"Привет, {userCfg.DisplayName}.",
            $"Доступ в интернет скоро закончится.",
            $"Осталось: примерно {remainingMinutes} мин",
            $"Отключение: {FormatLocalDateTime(expiresAtMs, cfg.TimeZone)}",
            $"Таймзона: {cfg.TimeZone.Id}"
        ]);

        return _email.Send(
            [userCfg.Email!],
            subject,
            body);
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
            // Active usage is counted only inside today's boundaries.
            activeMs = Math.Max(0, activeEnd - activeStart);
        }

        var usedSeconds = (int)((completedMs + activeMs) / 1000);
        return new UsageState(usedSeconds, active);
    }

    private static DayWindowBounds GetDayWindowBounds(AccessConfig cfg, DayContext day)
    {
        var window = cfg.DayWindows.GetValueOrDefault(day.DayKey) ?? new DayWindowConfig(0, (24 * 60) - 1);
        var startMs = day.StartOfDayMs + (window.StartMinutes * 60_000L);
        var endMs = day.StartOfDayMs + (window.EndMinutes * 60_000L);
        var rawCutoffMs = endMs + (DayEndCutoffGraceMinutes * 60_000L);
        var latestSameDayMs = day.NextDayMs - 60_000L;
        var cutoffMs = Math.Min(rawCutoffMs, latestSameDayMs);
        if (cutoffMs < endMs)
        {
            cutoffMs = endMs;
        }

        return new DayWindowBounds(startMs, endMs, cutoffMs);
    }

    private static string FormatLocalTime(long ms, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone);
        return local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatLocalDateTime(long ms, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone);
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatDurationHms(long durationMs)
    {
        var totalSeconds = Math.Max(0, durationMs / 1000L);
        var hours = totalSeconds / 3600L;
        var minutes = (totalSeconds % 3600L) / 60L;
        var seconds = totalSeconds % 60L;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    private static long ToUnixMilliseconds(TimeZoneInfo zone, DateTime localUnspecified)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(localUnspecified, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    private static string ResolveEndReason(SessionHistoryRecord session)
    {
        if (session.EndedAtMs is null)
        {
            return "active";
        }

        if (string.Equals(session.EndedReason, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return "manual";
        }

        return "expired";
    }

    private static string EndReasonLabel(string reason)
    {
        return reason switch
        {
            "manual" => "Отключено кнопкой",
            "expired" => "Истекло автоматически",
            "active" => "Активная сессия",
            _ => "Неизвестно"
        };
    }

    private static UserAuditEvent MapAuditEvent(AuditLogRecord row)
    {
        return new UserAuditEvent(
            row.Id,
            row.OccurredAtMs,
            row.Action,
            AuditActionLabel(row.Action),
            row.RequestedWindowMinutes,
            row.GrantedWindowMinutes,
            row.GrantedUntilMs,
            row.Details,
            AuditDetailsLabel(row.Details),
            0
        );
    }

    private static List<UserAuditEvent> BuildAuditEventsForStats(
        List<SessionHistoryRecord> sessions,
        List<AuditLogRecord> auditRows,
        long rangeStartUtcMs,
        long rangeEndUtcMs)
    {
        var events = auditRows.Select(MapAuditEvent).ToList();
        var eventKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            eventKeys.Add(MakeAuditEventKey(item.Action, item.OccurredAtMs));
        }

        long syntheticId = -1;
        foreach (var session in sessions)
        {
            if (session.StartedAtMs >= rangeStartUtcMs && session.StartedAtMs < rangeEndUtcMs)
            {
                var startKey = MakeAuditEventKey("request_started", session.StartedAtMs);
                if (!eventKeys.Contains(startKey))
                {
                    events.Add(new UserAuditEvent(
                        syntheticId--,
                        session.StartedAtMs,
                        "request_started",
                        AuditActionLabel("request_started"),
                        null,
                        null,
                        session.ExpiresAtMs,
                        "derived_from_session_history",
                        AuditDetailsLabel("derived_from_session_history"),
                        session.Id));
                    eventKeys.Add(startKey);
                }
            }

            if (session.EndedAtMs is null)
            {
                continue;
            }

            var endMs = session.EndedAtMs.Value;
            if (endMs < rangeStartUtcMs || endMs >= rangeEndUtcMs)
            {
                continue;
            }

            var endAction = string.Equals(session.EndedReason, "manual", StringComparison.OrdinalIgnoreCase)
                ? "disable"
                : "expired";
            var endKey = MakeAuditEventKey(endAction, endMs);
            if (eventKeys.Contains(endKey))
            {
                continue;
            }

            events.Add(new UserAuditEvent(
                syntheticId--,
                endMs,
                endAction,
                AuditActionLabel(endAction),
                null,
                null,
                null,
                "derived_from_session_history",
                AuditDetailsLabel("derived_from_session_history"),
                session.Id));
            eventKeys.Add(endKey);
        }

        var sessionsById = sessions.ToDictionary(s => s.Id);

        return events
            .Select(e => e with { SessionId = ResolveSessionId(e, sessions) })
            .Where(e => e.SessionId > 0)
            .Select(e => EnrichAuditEventWithSessionDuration(e, sessionsById))
            .OrderByDescending(e => e.OccurredAtMs)
            .ThenByDescending(e => e.Id)
            .ToList();
    }

    private static UserAuditEvent EnrichAuditEventWithSessionDuration(UserAuditEvent evt, Dictionary<long, SessionHistoryRecord> sessionsById)
    {
        if (!string.Equals(evt.Action, "expired", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(evt.Action, "disable", StringComparison.OrdinalIgnoreCase))
        {
            return evt;
        }

        if (!sessionsById.TryGetValue(evt.SessionId, out var session))
        {
            return evt;
        }

        var actualEndMs = session.EndedAtMs ?? session.ExpiresAtMs;
        var durationMs = Math.Max(0, actualEndMs - session.StartedAtMs);
        var durationLabel = $"Реальная длительность сессии: {FormatDurationHms(durationMs)}";

        var detailsLabel = string.IsNullOrWhiteSpace(evt.DetailsLabel)
            ? durationLabel
            : $"{evt.DetailsLabel}; {durationLabel}";

        return evt with { DetailsLabel = detailsLabel };
    }

    private static long ResolveSessionId(UserAuditEvent evt, List<SessionHistoryRecord> sessions)
    {
        if (evt.SessionId > 0)
        {
            return evt.SessionId;
        }

        const long tolMs = 2000;

        var linked = evt.Action switch
        {
            "request_started" => sessions
                .Where(s => Math.Abs(s.StartedAtMs - evt.OccurredAtMs) <= tolMs),
            "disable" => sessions
                .Where(s => s.EndedAtMs is not null
                    && string.Equals(s.EndedReason, "manual", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(s.EndedAtMs.Value - evt.OccurredAtMs) <= tolMs),
            "expired" => sessions
                .Where(s => s.EndedAtMs is not null
                    && !string.Equals(s.EndedReason, "manual", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(s.EndedAtMs.Value - evt.OccurredAtMs) <= tolMs),
            "window_extended" => sessions
                .Where(s => evt.OccurredAtMs >= s.StartedAtMs - tolMs && evt.OccurredAtMs <= (s.EndedAtMs ?? s.ExpiresAtMs) + tolMs),
            "window_shortened" => sessions
                .Where(s => evt.OccurredAtMs >= s.StartedAtMs - tolMs && evt.OccurredAtMs <= (s.EndedAtMs ?? s.ExpiresAtMs) + tolMs),
            "window_updated" => sessions
                .Where(s => evt.OccurredAtMs >= s.StartedAtMs - tolMs && evt.OccurredAtMs <= (s.EndedAtMs ?? s.ExpiresAtMs) + tolMs),
            "request" => sessions
                .Where(s => evt.OccurredAtMs >= s.StartedAtMs - tolMs && evt.OccurredAtMs <= (s.EndedAtMs ?? s.ExpiresAtMs) + tolMs),
            _ => Enumerable.Empty<SessionHistoryRecord>()
        };

        var session = linked
            .OrderByDescending(s => s.StartedAtMs)
            .FirstOrDefault();

        return session?.Id ?? 0;
    }

    private static string MakeAuditEventKey(string action, long occurredAtMs)
    {
        return $"{action}|{occurredAtMs}";
    }

    private static string AuditActionLabel(string action)
    {
        return action switch
        {
            "request_started" => "Доступ запрошен",
            "window_extended" => "Окно увеличено",
            "window_shortened" => "Окно уменьшено",
            "window_updated" => "Параметры окна обновлены",
            "disable" => "Отключено кнопкой",
            "disable_no_session" => "Отключение без активной сессии",
            "expired" => "Время истекло автоматически",
            "request" => "Доступ запрошен/изменен",
            _ => action
        };
    }

    private static string? AuditDetailsLabel(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        return details switch
        {
            "window_clipped_by_limits" => "Окно ограничено остатком лимита/границей дня",
            "window_applied" => "Окно применено",
            "active_session_stopped" => "Активная сессия остановлена",
            "no_active_session" => "Активной сессии не было",
            "derived_from_session_history" => "Событие восстановлено из истории сессий",
            _ => details
        };
    }

    private static DayContext GetDayContext(TimeZoneInfo zone)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var dayKey = DayKeyFromDayOfWeek(localNow.DayOfWeek);

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

    private static string GetDayKeyForTimestamp(long utcMs, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(utcMs), zone);
        return DayKeyFromDayOfWeek(local.DayOfWeek);
    }

    private static string DayKeyFromDayOfWeek(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
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
    }

    private sealed record DayContext(long NowMs, string DayKey, long StartOfDayMs, long NextDayMs);
    private sealed record DayWindowBounds(long StartMs, long EndMs, long CutoffMs);
    private sealed record UsageState(int UsedSeconds, SessionRecord? ActiveSession);
}
