sealed record AccessConfig(
    TimeZoneInfo TimeZone,
    int DefaultWindowMinutes,
    int GraceMinutes,
    int EndingSoonMinutes,
    Dictionary<string, DayWindowConfig> DayWindows,
    List<UserLimitConfig> Users);
sealed record DayWindowConfig(int StartMinutes, int EndMinutes);
sealed record UserLimitConfig(
    string Name,
    string DisplayName,
    string? Email,
    List<string> ParentEmails,
    int DefaultWindowMinutes,
    Dictionary<string, int> LimitsMinutes);
sealed record SessionRecord(long Id, string UserName, long StartedAtMs, long ExpiresAtMs);
sealed record SoonEndingSessionRecord(long Id, string UserName, long ExpiresAtMs);
sealed record SessionHistoryRecord(long Id, long StartedAtMs, long ExpiresAtMs, long? EndedAtMs, string? EndedReason);
sealed record AuditLogRecord(long Id, string Action, int? RequestedWindowMinutes, int? GrantedWindowMinutes, long? GrantedUntilMs, string? Details, long OccurredAtMs);
sealed record ActiveSessionRow(long Id, long StartedAtMs, long EndsAtMs, int RemainingSeconds, int TotalSeconds);
sealed record UserStateRow(
    string Name,
    string DisplayName,
    bool ExistsInMikrotik,
    string? MikrotikId,
    bool MikrotikDisabled,
    bool MikrotikPaused,
    bool MikrotikBlocked,
    bool MikrotikActive,
    string MikrotikStatus,
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
    long DayWindowStartMs,
    long DayWindowEndMs,
    long DayWindowCutoffMs,
    string DayWindowStart,
    string DayWindowEnd,
    string DayWindowCutoff,
    int DefaultWindowMinutes,
    int GraceMinutes,
    List<UserStateRow> Users
);

sealed record RequestWindowDto(int? WindowMinutes);
sealed record RequestResponse(bool Ok, string User, int RequestedWindowMinutes, long GrantedUntilMs, StateResponse State);
sealed record DisableResponse(bool Ok, string User, StateResponse State);
sealed record UserStatsSegment(long SessionId, long StartedAtMs, long EndedAtMs, string EndReason, string EndReasonLabel);
sealed record UserAuditEvent(
    long Id,
    long OccurredAtMs,
    string Action,
    string ActionLabel,
    int? RequestedWindowMinutes,
    int? GrantedWindowMinutes,
    long? GrantedUntilMs,
    string? Details,
    string? DetailsLabel,
    long SessionId
);
sealed record UserStatsResponse(
    string User,
    string DisplayName,
    string Timezone,
    int Days,
    long ServerTimeMs,
    long RangeStartMs,
    long RangeEndMs,
    List<UserStatsSegment> Segments,
    List<UserAuditEvent> AuditEvents
);
