using System.Globalization;
using System.Text.RegularExpressions;

sealed partial class GradeLimitPolicyEvaluator
{
    private readonly CurrentGradesFileReader _reader;
    private readonly ILogger<GradeLimitPolicyEvaluator> _logger;

    public GradeLimitPolicyEvaluator(
        CurrentGradesFileReader reader,
        ILogger<GradeLimitPolicyEvaluator> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public Task<GradeLimitDecision> EvaluateAsync(
        UserLimitConfig user,
        int baseLimitMinutes,
        int graceMinutes,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        return EvaluateAsync(
            user,
            baseLimitMinutes,
            graceMinutes,
            timeZone,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    internal async Task<GradeLimitDecision> EvaluateAsync(
        UserLimitConfig user,
        int baseLimitMinutes,
        int graceMinutes,
        TimeZoneInfo timeZone,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var policy = user.GradeLimitPolicy;
        if (policy?.Enabled != true)
        {
            return Decision(
                user.Name,
                baseLimitMinutes,
                baseLimitMinutes,
                baseLimitMinutes + graceMinutes,
                null,
                null,
                null,
                [],
                "NotConfigured",
                null);
        }

        var readResult = await _reader.ReadAsync(cancellationToken);
        if (!readResult.Success)
        {
            LogSourceError(user.Name, readResult.ErrorCode, readResult.ErrorMessage);
            return Unavailable(
                user.Name,
                baseLimitMinutes,
                policy.Threshold,
                policy.NormalDecisionTtlMinutes,
                readResult.ErrorCode);
        }

        var snapshot = readResult.Snapshot!;
        if (!string.Equals(snapshot.UserName.Trim(), user.Name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            const string errorCode = "user_mismatch";
            LogSourceError(user.Name, errorCode, "Имя пользователя в файле оценок не совпадает с конфигурацией");
            return Unavailable(
                user.Name,
                baseLimitMinutes,
                policy.Threshold,
                policy.NormalDecisionTtlMinutes,
                errorCode);
        }

        if (!TryParseSourceTime(snapshot.AsOf, timeZone, out var sourceAsOfUtc))
        {
            const string errorCode = "as_of_invalid";
            LogSourceError(user.Name, errorCode, "Поле asOf в файле оценок имеет неподдерживаемый формат");
            return Unavailable(
                user.Name,
                baseLimitMinutes,
                policy.Threshold,
                policy.NormalDecisionTtlMinutes,
                errorCode);
        }

        var lowGrades = snapshot.CurrentGrades
            .Where(grade => grade.AveragePercentage is not null && grade.AveragePercentage.Value < policy.Threshold)
            .Select(grade => new LowGradeRow(grade.ClassId, grade.AveragePercentage!.Value))
            .ToList();
        var stale = nowUtc - sourceAsOfUtc > TimeSpan.FromMinutes(policy.NormalDecisionTtlMinutes);
        var restrictedLimitMinutes = Math.Min(baseLimitMinutes, policy.RestrictedLimitMinutes);

        if (lowGrades.Count > 0)
        {
            return Decision(
                user.Name,
                baseLimitMinutes,
                restrictedLimitMinutes,
                restrictedLimitMinutes,
                policy.Threshold,
                policy.NormalDecisionTtlMinutes,
                sourceAsOfUtc,
                lowGrades,
                stale ? "RestrictedStale" : "Restricted",
                null);
        }

        if (stale)
        {
            const string errorCode = "stale_normal";
            LogSourceError(user.Name, errorCode, "Снимок оценок без плохих оценок устарел");
            return Unavailable(
                user.Name,
                baseLimitMinutes,
                policy.Threshold,
                policy.NormalDecisionTtlMinutes,
                errorCode,
                sourceAsOfUtc);
        }

        return Decision(
            user.Name,
            baseLimitMinutes,
            baseLimitMinutes,
            baseLimitMinutes + graceMinutes,
            policy.Threshold,
            policy.NormalDecisionTtlMinutes,
            sourceAsOfUtc,
            [],
            "Normal",
            null);
    }

    internal static bool TryParseSourceTime(string raw, TimeZoneInfo timeZone, out DateTimeOffset utc)
    {
        var value = raw.Trim();
        if (IsoOffsetSuffixRegex().IsMatch(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            utc = timestamp.ToUniversalTime();
            return true;
        }

        if (DateTime.TryParseExact(
            value,
            "MM/dd/yyyy hh:mm tt",
            CultureInfo.GetCultureInfo("en-US"),
            DateTimeStyles.AllowWhiteSpaces,
            out var localTime))
        {
            var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
            if (!timeZone.IsInvalidTime(unspecified))
            {
                utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone), TimeSpan.Zero);
                return true;
            }
        }

        utc = default;
        return false;
    }

    private void LogSourceError(string userName, string? errorCode, string? message)
    {
        _logger.LogWarning(
            "Grade limit policy source error for {UserName}: {ErrorCode}. {Message}",
            userName,
            errorCode ?? "unknown",
            message ?? "No details");
    }

    private static GradeLimitDecision Unavailable(
        string userName,
        int baseLimitMinutes,
        double threshold,
        int normalDecisionTtlMinutes,
        string? errorCode,
        DateTimeOffset? sourceAsOfUtc = null)
    {
        return Decision(
            userName,
            baseLimitMinutes,
            0,
            0,
            threshold,
            normalDecisionTtlMinutes,
            sourceAsOfUtc,
            [],
            "SourceUnavailable",
            errorCode ?? "source_unavailable");
    }

    private static GradeLimitDecision Decision(
        string userName,
        int baseLimitMinutes,
        int effectiveLimitMinutes,
        int hardCapMinutes,
        double? threshold,
        int? normalDecisionTtlMinutes,
        DateTimeOffset? sourceAsOfUtc,
        List<LowGradeRow> lowGrades,
        string status,
        string? errorCode)
    {
        return new GradeLimitDecision(
            userName,
            baseLimitMinutes,
            effectiveLimitMinutes,
            hardCapMinutes,
            threshold,
            normalDecisionTtlMinutes,
            sourceAsOfUtc,
            lowGrades,
            status,
            errorCode);
    }

    [GeneratedRegex(@"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoOffsetSuffixRegex();
}
