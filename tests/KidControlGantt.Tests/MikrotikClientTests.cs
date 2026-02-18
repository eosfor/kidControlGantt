using System.Net;
using System.Text;
using System.Text.Json;

public sealed class MikrotikClientTests
{
    [Fact]
    public async Task GetKidControlListAsync_ParsesResponseRows()
    {
        var handler = new RecordingHandler(static (_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{".id":"*1","name":"test","paused":true}]""", Encoding.UTF8, "application/json")
            });

        using var http = new HttpClient(handler);
        var client = new MikrotikClient(http, TestRuntimeFactory.Create("/tmp/test.db", "/tmp/config.json"));

        var result = await client.GetKidControlListAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("*1", result[0][".id"]);
        Assert.Equal("test", result[0]["name"]);
        Assert.Equal("true", result[0]["paused"]);
    }

    [Fact]
    public async Task ApplyWindowAsync_SendsPatchThenResume()
    {
        var handler = new RecordingHandler(static (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var client = new MikrotikClient(http, TestRuntimeFactory.Create("/tmp/test.db", "/tmp/config.json"));
        var entry = new Dictionary<string, string>(StringComparer.Ordinal) { [".id"] = "*3" };

        var startMs = DateTimeOffset.Parse("2026-01-01T10:00:00Z").ToUnixTimeMilliseconds();
        var endMs = DateTimeOffset.Parse("2026-01-01T11:30:00Z").ToUnixTimeMilliseconds();

        await client.ApplyWindowAsync(entry, "mon", startMs, endMs, TimeZoneInfo.Utc, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);

        var patch = handler.Requests[0];
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Equal("http://mikrotik.local/rest/ip/kid-control/*3", patch.Url);
        using (var patchJson = JsonDocument.Parse(patch.Body))
        {
            Assert.Equal("10h-11h30m", patchJson.RootElement.GetProperty("mon").GetString());
            Assert.Equal("false", patchJson.RootElement.GetProperty("disabled").GetString());
        }

        var resume = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, resume.Method);
        Assert.Equal("http://mikrotik.local/rest/ip/kid-control/resume", resume.Url);
        using var resumeJson = JsonDocument.Parse(resume.Body);
        Assert.Equal("*3", resumeJson.RootElement.GetProperty("numbers").GetString());
    }

    [Fact]
    public async Task DisableUserAsync_SendsPatchThenPause()
    {
        var handler = new RecordingHandler(static (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var client = new MikrotikClient(http, TestRuntimeFactory.Create("/tmp/test.db", "/tmp/config.json"));
        var entry = new Dictionary<string, string>(StringComparer.Ordinal) { [".id"] = "*7" };

        await client.DisableUserAsync(entry, "tue", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);

        var patch = handler.Requests[0];
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Equal("http://mikrotik.local/rest/ip/kid-control/*7", patch.Url);
        using (var patchJson = JsonDocument.Parse(patch.Body))
        {
            Assert.Equal(string.Empty, patchJson.RootElement.GetProperty("tue").GetString());
            Assert.Equal("false", patchJson.RootElement.GetProperty("disabled").GetString());
        }

        var pause = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, pause.Method);
        Assert.Equal("http://mikrotik.local/rest/ip/kid-control/pause", pause.Url);
        using var pauseJson = JsonDocument.Parse(pause.Body);
        Assert.Equal("*7", pauseJson.RootElement.GetProperty("numbers").GetString());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder = responder;
        private int _requestCounter;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri?.ToString() ?? string.Empty, body));

            var current = Interlocked.Increment(ref _requestCounter) - 1;
            return _responder(request, current);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Url, string Body);
}
