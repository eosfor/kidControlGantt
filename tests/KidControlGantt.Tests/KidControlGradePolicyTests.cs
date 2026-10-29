using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class KidControlGradePolicyTests
{
    [Fact]
    public async Task RequestAccess_WhenExistingSessionExceedsRestrictedLimit_DoesNotChangeSessionOrMikrotik()
    {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "kid-access-config.json");
        var gradesPath = Path.Combine(tmp.Path, "currentGrades.json");
        var dbPath = Path.Combine(tmp.Path, "state.db");
        File.WriteAllText(configPath, """
        {
          "timezone": "UTC",
          "graceMinutes": 15,
          "users": [
            {
              "name": "Vlad",
              "defaultWindowMinutes": 120,
              "limitsMinutes": {
                "mon": 600, "tue": 600, "wed": 600, "thu": 600,
                "fri": 600, "sat": 600, "sun": 600
              },
              "gradeLimitPolicy": {
                "enabled": true,
                "threshold": 0.82,
                "restrictedLimitMinutes": 300,
                "normalDecisionTtlMinutes": 10080
              }
            }
          ]
        }
        """);
        File.WriteAllText(gradesPath, $$"""
        {
          "userName": "Vlad",
          "asOf": "{{DateTimeOffset.UtcNow:O}}",
          "currentGrades": [
            { "classId": "Math", "averagePercentage": 0.80, "mark": "B", "missing": 0, "lastUpdated": null }
          ]
        }
        """);

        var settings = TestRuntimeFactory.Create(dbPath, configPath, gradesPath);
        var repository = new SessionRepository(settings);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var originalExpiryMs = nowMs + 600 * 60_000L;
        repository.InsertSession("Vlad", nowMs, originalExpiryMs, nowMs);

        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var reader = new CurrentGradesFileReader(settings);
        var service = new KidControlService(
            new AccessConfigProvider(settings),
            new GradeLimitPolicyEvaluator(reader, NullLogger<GradeLimitPolicyEvaluator>.Instance),
            repository,
            new MikrotikClient(http, settings),
            new EmailNotificationService(settings, NullLogger<EmailNotificationService>.Instance),
            NullLogger<KidControlService>.Instance);

        var state = await service.GetStateAsync(CancellationToken.None);
        var userState = Assert.Single(state.Users);
        Assert.Equal(0.82, userState.GradeRestrictionThreshold);
        Assert.Equal(10_080, userState.GradeRestrictionNormalTtlMinutes);
        var requestCountBeforeExtension = handler.RequestCount;

        var exception = await Assert.ThrowsAsync<AppHttpException>(
            () => service.RequestAccessAsync("Vlad", 300, CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Contains("остается без изменений", exception.Message);
        Assert.Equal(originalExpiryMs, repository.GetActiveSession("Vlad")?.ExpiresAtMs);
        Assert.Equal(requestCountBeforeExtension, handler.RequestCount);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kid-control-service-policy-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Temp cleanup is best-effort in tests.
            }
        }
    }
}
