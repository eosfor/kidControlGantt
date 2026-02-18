using System.Text;
using System.Text.Json;

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
