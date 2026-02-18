sealed record MikrotikStatusInfo(
    bool Disabled,
    bool Paused,
    bool Blocked,
    bool Active,
    string Status
);

static class MikrotikStatusMapper
{
    public static MikrotikStatusInfo FromEntry(Dictionary<string, string>? entry)
    {
        if (entry is null)
        {
            return new MikrotikStatusInfo(
                Disabled: false,
                Paused: false,
                Blocked: false,
                Active: false,
                Status: "unknown");
        }

        var disabled = ParseBool(entry.GetValueOrDefault("disabled"));
        var paused = ParseBool(entry.GetValueOrDefault("paused"));
        var blocked = ParseBool(entry.GetValueOrDefault("blocked"));

        // RouterOS docs expose flags (B/P/X), where combinations are possible.
        // Treat active as "no blocking flags are set".
        var active = !disabled && !paused && !blocked;

        var parts = new List<string>();
        if (active)
        {
            parts.Add("active");
        }
        if (blocked)
        {
            parts.Add("blocked");
        }
        if (paused)
        {
            parts.Add("paused");
        }
        if (disabled)
        {
            parts.Add("disabled");
        }

        return new MikrotikStatusInfo(
            Disabled: disabled,
            Paused: paused,
            Blocked: blocked,
            Active: active,
            Status: parts.Count == 0 ? "unknown" : string.Join("+", parts));
    }

    private static bool ParseBool(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
