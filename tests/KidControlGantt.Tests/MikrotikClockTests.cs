using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class MikrotikClockTests
{
    [Theory]
    [InlineData("2026-10-27", "12:00:00", "+00:00")]
    [InlineData("oct/27/2026", "05:00:00", "-07:00")]
    [InlineData("2026-10-27", "17:30:00", "+05:30")]
    [InlineData("2026-10-28", "02:00:00", "+14:00")]
    public async Task GetClock_ParsesAbsoluteTimeUsingRouterOffset(string date, string time, string offset)
    {
        using var f = new Fixture();
        f.Handler.Body = $$"""{"date":"{{date}}","time":"{{time}}","gmt-offset":"{{offset}}","dst-active":"true"}""";
        var result = await f.Client.GetClockAsync(CancellationToken.None);
        Assert.Equal(f.Clock.Now.ToUnixTimeMilliseconds(), result);
        Assert.Equal("http://mikrotik.local/proxy/rest/system/clock", Assert.Single(f.Handler.Requests));
    }

    [Fact]
    public async Task GetClock_AcceptsSingleRecordArray()
    {
        using var f = new Fixture();
        f.Handler.Body = """[{"date":"2026-10-27","time":"12:00:00","gmt-offset":"+00:00"}]""";
        Assert.Equal(f.Clock.Now.ToUnixTimeMilliseconds(), await f.Client.GetClockAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("[{},{}]")]
    [InlineData("{}")]
    [InlineData("""{"date":"2026-10-27","time":"12:00:00"}""")]
    [InlineData("""{"date":"2026-02-30","time":"12:00:00","gmt-offset":"+00:00"}""")]
    [InlineData("""{"date":"2026-10-27","time":"25:00:00","gmt-offset":"+00:00"}""")]
    [InlineData("""{"date":"2026-10-27","time":"12:00:00","gmt-offset":"+14:30"}""")]
    [InlineData("""{"date":"2026-10-27","time":"12:00:00","gmt-offset":"--07:00"}""")]
    [InlineData("""{"date":20261027,"time":"12:00:00","gmt-offset":"+00:00"}""")]
    public async Task GetClock_RejectsInvalidOrIncompleteResponse(string body)
    {
        using var f = new Fixture();
        f.Handler.Body = body;
        var error = await Assert.ThrowsAsync<AppHttpException>(() => f.Client.GetClockAsync(CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task Service_ReturnsServerAndRouterSamplesFromMiddleOfRequest()
    {
        using var f = new Fixture();
        f.Handler.Body = """{"date":"2026-10-27","time":"12:00:02","gmt-offset":"+00:00"}""";
        f.Handler.OnRequest = _ => f.Clock.Now = f.Clock.Now.AddSeconds(4);
        var result = await f.Service.GetClockAsync(CancellationToken.None);
        Assert.Equal(f.Clock.Now.AddSeconds(-2).ToUnixTimeMilliseconds(), result.ServerTimeMs);
        Assert.Equal(result.ServerTimeMs, result.RouterTimeMs);
        Assert.Equal("UTC", result.Timezone);
        Assert.Null(result.RouterError);
        Assert.Single(f.Handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Service_RouterErrorKeepsServerClockAvailable(HttpStatusCode status)
    {
        using var f = new Fixture();
        f.Handler.Status = status;
        var result = await f.Service.GetClockAsync(CancellationToken.None);
        Assert.Equal(f.Clock.Now.ToUnixTimeMilliseconds(), result.ServerTimeMs);
        Assert.Null(result.RouterTimeMs);
        Assert.False(string.IsNullOrWhiteSpace(result.RouterError));
    }

    [Fact]
    public async Task Service_NetworkFailureIsDisplayedAsUnavailable()
    {
        using var f = new Fixture();
        f.Handler.OnRequest = _ => throw new HttpRequestException("injected connection failure");
        var result = await f.Service.GetClockAsync(CancellationToken.None);
        Assert.Null(result.RouterTimeMs);
        Assert.NotNull(result.RouterError);
    }

    [Fact]
    public async Task Service_CallerCancellationPropagates()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Handler.OnRequest = ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.GetClockAsync(cts.Token));
    }

    [Fact]
    public async Task Service_TimeoutKeepsServerClockAvailable()
    {
        using var f = new Fixture();
        f.Handler.OnRequest = _ => throw new TaskCanceledException("injected timeout");
        var result = await f.Service.GetClockAsync(CancellationToken.None);
        Assert.Null(result.RouterTimeMs);
        Assert.NotNull(result.RouterError);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kid-clock-{Guid.NewGuid():N}");
        private readonly HttpClient _http;
        public MutableClock Clock { get; } = new();
        public ClockHandler Handler { get; } = new();
        public MikrotikClient Client { get; }
        public KidControlService Service { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_dir);
            var config = Path.Combine(_dir, "config.json");
            File.WriteAllText(config, """{"timezone":"UTC","users":[]}""");
            var settings = TestRuntimeFactory.Create(Path.Combine(_dir, "state.db"), config,
                mikrotikKidControlUrl: "http://mikrotik.local/proxy/rest/ip/kid-control");
            _http = new HttpClient(Handler);
            Client = new MikrotikClient(_http, settings, Clock);
            Service = new KidControlService(new AccessConfigProvider(settings),
                new GradeLimitPolicyEvaluator(new CurrentGradesFileReader(settings), NullLogger<GradeLimitPolicyEvaluator>.Instance),
                new SessionRepository(settings), Client,
                new EmailNotificationService(settings, NullLogger<EmailNotificationService>.Instance),
                NullLogger<KidControlService>.Instance, Clock);
        }
        public void Dispose() { _http.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ClockHandler : HttpMessageHandler
    {
        public string Body = """{"date":"2026-10-27","time":"12:00:00","gmt-offset":"+00:00"}""";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Action<CancellationToken>? OnRequest;
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Requests.Add(request.RequestUri!.ToString());
            OnRequest?.Invoke(ct);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
}
