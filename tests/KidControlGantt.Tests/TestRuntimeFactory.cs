internal static class TestRuntimeFactory
{
    public static RuntimeSettings Create(string dbPath, string limitsConfigPath, string? currentGradesPath = null,
        string? mikrotikKidControlUrl = null)
    {
        return new RuntimeSettings
        {
            MikrotikKidControlUrl = mikrotikKidControlUrl ?? "http://mikrotik.local/rest/ip/kid-control",
            AuthorizationHeader = string.Empty,
            DbPath = dbPath,
            LimitsConfigPath = limitsConfigPath,
            CurrentGradesPath = currentGradesPath
                ?? Path.Combine(Path.GetDirectoryName(limitsConfigPath) ?? "/tmp", "currentGrades.json"),
            ConfigCacheTtlMs = 5000,
            SweepIntervalSeconds = 10,
            TimezoneOverride = string.Empty,
            SmtpHost = string.Empty,
            SmtpPort = 587,
            SmtpUser = string.Empty,
            SmtpPassword = string.Empty,
            SmtpFrom = string.Empty,
            SmtpUseSsl = true
        };
    }
}
