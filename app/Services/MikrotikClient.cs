using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed class MikrotikClient
{
    private static readonly string[] DayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public MikrotikClient(HttpClient http, RuntimeSettings settings, TimeProvider? clock = null)
    {
        _http = http;
        _clock = clock ?? TimeProvider.System;
        ApiUrl = settings.MikrotikKidControlUrl;
        AuthorizationHeader = settings.AuthorizationHeader;
    }

    public string ApiUrl { get; }
    public string AuthorizationHeader { get; }

    public async Task<long> GetClockAsync(CancellationToken ct)
    {
        // Keep any reverse-proxy prefix before /rest when selecting the sibling menu.
        var url = new Uri(new Uri(ApiUrl.TrimEnd('/') + "/"), "../../system/clock");
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AppHttpException(502, $"MikroTik clock request returned HTTP {(int)response.StatusCode}");
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(body);
            var clock = document.RootElement;
            if (clock.ValueKind == JsonValueKind.Array && clock.GetArrayLength() == 1)
            {
                clock = clock[0];
            }
            var dateText = ReadClockField(clock, "date");
            var timeText = ReadClockField(clock, "time");
            var offsetText = ReadClockField(clock, "gmt-offset");
            var unsignedOffset = offsetText.StartsWith('+') || offsetText.StartsWith('-') ? offsetText[1..] : offsetText;
            if (!DateOnly.TryParseExact(dateText, ["yyyy-MM-dd", "MMM/dd/yyyy", "MMM/d/yyyy"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !TimeOnly.TryParseExact(timeText, ["HH:mm:ss", "H:mm:ss"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                || !TimeSpan.TryParseExact(unsignedOffset, @"hh\:mm", CultureInfo.InvariantCulture, out var offset)
                || offset > TimeSpan.FromHours(14))
            {
                throw new AppHttpException(502, "MikroTik clock contains an invalid date, time or UTC offset");
            }
            if (offsetText.StartsWith('-'))
            {
                offset = -offset;
            }
            // gmt-offset already includes DST; do not apply the browser or server zone.
            return new DateTimeOffset(date.ToDateTime(time), offset).ToUnixTimeMilliseconds();
        }
        catch (JsonException)
        {
            throw new AppHttpException(502, "MikroTik clock returned invalid JSON");
        }
    }

    private static string ReadClockField(JsonElement clock, string name)
    {
        if (clock.ValueKind == JsonValueKind.Object && clock.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String && field.GetString() is string value && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        throw new AppHttpException(502, $"MikroTik clock is missing {name}");
    }

    public bool MatchesSession(Dictionary<string, string> entry, SessionRecord? session, TimeZoneInfo zone)
    {
        return MatchesSchedule(entry, BuildSchedule(session, zone))
            && entry.TryGetValue("paused", out var paused)
            && (session is null ? paused is "true" or "yes" : paused is "false" or "no");
    }

    private static bool MatchesSchedule(Dictionary<string, string> entry, Dictionary<string, string> patch) =>
        patch.All(p => p.Key == "disabled"
            ? entry.TryGetValue(p.Key, out var disabled) && disabled is "false" or "no"
            : SameWindow(entry.GetValueOrDefault(p.Key) ?? string.Empty, p.Value));

    public async Task SynchronizeSessionAsync(Dictionary<string, string> entry, SessionRecord? session, TimeZoneInfo zone, CancellationToken ct)
    {
        var patch = BuildSchedule(session, zone);
        // Block first: a failed PATCH must not leave an older, broader schedule running.
        await PauseUserAsync(entry, ct);
        await PatchEntryAsync(entry, patch, ct);
        var entries = await GetKidControlListAsync(ct);
        var matches = entries.Where(e => e.GetValueOrDefault(".id") == GetEntryId(entry)).ToList();
        if (matches.Count != 1 || !MatchesSchedule(matches[0], patch)
            || matches[0].GetValueOrDefault("paused") is not ("true" or "yes"))
        {
            throw new AppHttpException(502, "MikroTik schedule verification failed; resume was not attempted");
        }
        if (session is not null && session.ExpiresAtMs > _clock.GetUtcNow().ToUnixTimeMilliseconds())
        {
            await ResumeUserAsync(entry, ct);
        }
    }

    private static Dictionary<string, string> BuildSchedule(SessionRecord? session, TimeZoneInfo zone)
    {
        var patch = new Dictionary<string, string> { ["disabled"] = "false" };
        foreach (var day in DayKeys)
        {
            patch[day] = string.Empty;
            // Unlimited-rate windows can independently allow access.
            patch[$"tur-{day}"] = string.Empty;
        }
        if (session is not null)
        {
            var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(session.StartedAtMs), zone);
            var end = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(session.ExpiresAtMs), zone);
            if (end <= start || end.Date != start.Date)
            {
                throw new InvalidOperationException($"Session {session.Id} must have a positive, same-day interval");
            }
            var day = start.DayOfWeek == DayOfWeek.Sunday ? "sun" : DayKeys[(int)start.DayOfWeek - 1];
            patch[day] = BuildWindowValue(session.StartedAtMs, session.ExpiresAtMs, zone);
        }
        return patch;
    }

    private static bool SameWindow(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return true;
        }
        return TryWindow(actual, out var actualWindow) && TryWindow(expected, out var expectedWindow)
            && actualWindow == expectedWindow;
    }

    private static bool TryWindow(string value, out (int Start, int End) window)
    {
        window = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        var parts = value.Split('-');
        if (parts.Length != 2 || !TryTime(parts[0], out var start) || !TryTime(parts[1], out var end))
        {
            return false;
        }
        window = (start, end);
        return true;
    }

    private static bool TryTime(string value, out int seconds)
    {
        seconds = 0;
        if (TimeSpan.TryParseExact(value, [@"h\:mm", @"h\:mm\:ss", @"hh\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var time))
        {
            seconds = (int)time.TotalSeconds;
            return true;
        }
        var match = Regex.Match(value, @"^(?:(\d{1,2})h)?(?:(\d{1,2})m)?(?:(\d{1,2})s)?$", RegexOptions.CultureInvariant);
        if (!match.Success || value.Length == 0)
        {
            return false;
        }
        for (var i = 1; i <= 3; i++)
        {
            if (match.Groups[i].Success)
            {
                seconds += int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) * (i == 1 ? 3600 : i == 2 ? 60 : 1);
            }
        }
        return true;
    }

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
        await ResumeUserAsync(entry, ct);
    }

    public async Task DisableUserAsync(Dictionary<string, string> entry, string dayKey, CancellationToken ct)
    {
        var patch = new Dictionary<string, string>
        {
            [dayKey] = string.Empty,
            ["disabled"] = "false"
        };

        await PatchEntryAsync(entry, patch, ct);
        await PauseUserAsync(entry, ct);
    }

    public async Task PauseUserAsync(Dictionary<string, string> entry, CancellationToken ct)
    {
        var id = GetEntryId(entry);
        // "numbers" targets a single Kid Control row, so pause applies only to that user.
        await RunCommandAsync("pause", new Dictionary<string, string> { ["numbers"] = id }, ct);
    }

    public async Task ResumeUserAsync(Dictionary<string, string> entry, CancellationToken ct)
    {
        var id = GetEntryId(entry);
        await RunCommandAsync("resume", new Dictionary<string, string> { ["numbers"] = id }, ct);
    }

    private async Task PatchEntryAsync(Dictionary<string, string> entry, Dictionary<string, string> patch, CancellationToken ct)
    {
        var id = GetEntryId(entry);
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

    private async Task RunCommandAsync(string command, Dictionary<string, string> payload, CancellationToken ct)
    {
        var url = $"{ApiUrl.TrimEnd('/')}/{command}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new AppHttpException(502, $"MikroTik POST {url} => {(int)res.StatusCode}. {body}".Trim());
        }
    }

    private static string GetEntryId(Dictionary<string, string> entry)
    {
        if (!entry.TryGetValue(".id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            throw new AppHttpException(502, "MikroTik entry не содержит .id");
        }

        return id;
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
