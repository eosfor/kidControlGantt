using Microsoft.Extensions.Logging.Abstractions;

public sealed class GradeLimitPolicyEvaluatorTests
{
    [Fact]
    public async Task Evaluate_NormalSnapshot_UsesBaseLimitAndGraceHardCap()
    {
        using var tmp = new TempDir();
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var evaluator = CreateEvaluator(tmp, now, 0.95);

        var result = await evaluator.EvaluateAsync(
            CreateUser(),
            600,
            15,
            TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"),
            now,
            CancellationToken.None);

        Assert.Equal("Normal", result.Status);
        Assert.Equal(600, result.EffectiveLimitMinutes);
        Assert.Equal(615, result.HardCapMinutes);
        Assert.Equal(0.85, result.Threshold);
        Assert.Equal(20_160, result.NormalDecisionTtlMinutes);
        Assert.Empty(result.LowGrades);
    }

    [Fact]
    public async Task Evaluate_RestrictedLimitAboveBaseLimit_DoesNotIncreaseAccess()
    {
        using var tmp = new TempDir();
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var evaluator = CreateEvaluator(tmp, now, 0.80);

        var result = await evaluator.EvaluateAsync(
            CreateUser(),
            120,
            15,
            TimeZoneInfo.Utc,
            now,
            CancellationToken.None);

        Assert.Equal("Restricted", result.Status);
        Assert.Equal(120, result.EffectiveLimitMinutes);
        Assert.Equal(120, result.HardCapMinutes);
    }

    [Fact]
    public async Task Evaluate_LowGrade_UsesRestrictedLimitWithoutGrace()
    {
        using var tmp = new TempDir();
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var evaluator = CreateEvaluator(tmp, now, 0.8499, includeNullGrade: true);

        var result = await evaluator.EvaluateAsync(
            CreateUser(),
            600,
            15,
            TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"),
            now,
            CancellationToken.None);

        Assert.Equal("Restricted", result.Status);
        Assert.Equal(300, result.EffectiveLimitMinutes);
        Assert.Equal(300, result.HardCapMinutes);
        var lowGrade = Assert.Single(result.LowGrades);
        Assert.Equal("Math", lowGrade.ClassId);
        Assert.Equal(0.8499, lowGrade.AveragePercentage);
    }

    [Fact]
    public async Task Evaluate_GradeAtThreshold_IsNormal()
    {
        using var tmp = new TempDir();
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var evaluator = CreateEvaluator(tmp, now, 0.85);

        var result = await evaluator.EvaluateAsync(
            CreateUser(),
            600,
            15,
            TimeZoneInfo.Utc,
            now,
            CancellationToken.None);

        Assert.Equal("Normal", result.Status);
    }

    [Fact]
    public async Task Evaluate_ReturnsConfiguredThresholdAndTtlForUi()
    {
        using var tmp = new TempDir();
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var evaluator = CreateEvaluator(tmp, now, 0.91);

        var result = await evaluator.EvaluateAsync(
            CreateUser(threshold: 0.92, normalDecisionTtlMinutes: 10_080),
            600,
            15,
            TimeZoneInfo.Utc,
            now,
            CancellationToken.None);

        Assert.Equal("Restricted", result.Status);
        Assert.Equal(0.92, result.Threshold);
        Assert.Equal(10_080, result.NormalDecisionTtlMinutes);
    }

    [Fact]
    public async Task Evaluate_StaleNormal_BlocksNewRequests()
    {
        using var tmp = new TempDir();
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 1, TimeSpan.Zero);
        var sourceTime = now.AddDays(-14).AddSeconds(-1);
        var evaluator = CreateEvaluator(tmp, sourceTime, 0.95);

        var result = await evaluator.EvaluateAsync(
            CreateUser(),
            600,
            15,
            TimeZoneInfo.Utc,
            now,
            CancellationToken.None);

        Assert.Equal("SourceUnavailable", result.Status);
        Assert.Equal("stale_normal", result.ErrorCode);
        Assert.Equal(0, result.EffectiveLimitMinutes);
        Assert.Equal(0, result.HardCapMinutes);
    }

    [Fact]
    public async Task Evaluate_StaleLowGrade_RemainsRestricted()
    {
        using var tmp = new TempDir();
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 1, TimeSpan.Zero);
        var evaluator = CreateEvaluator(tmp, now.AddDays(-30), 0.80);

        var result = await evaluator.EvaluateAsync(
            CreateUser(),
            600,
            15,
            TimeZoneInfo.Utc,
            now,
            CancellationToken.None);

        Assert.Equal("RestrictedStale", result.Status);
        Assert.Equal(300, result.EffectiveLimitMinutes);
        Assert.Equal(300, result.HardCapMinutes);
    }

    [Fact]
    public async Task Evaluate_InvalidAsOf_ReturnsSourceUnavailable()
    {
        using var tmp = new TempDir();
        WriteGrades(tmp, "not-a-date", 0.80);
        var evaluator = CreateEvaluator(tmp);

        var result = await evaluator.EvaluateAsync(
            CreateUser(),
            600,
            15,
            TimeZoneInfo.Utc,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal("SourceUnavailable", result.Status);
        Assert.Equal("as_of_invalid", result.ErrorCode);
    }

    [Fact]
    public void TryParseSourceTime_LocalFormat_UsesConfiguredTimeZone()
    {
        var parsed = GradeLimitPolicyEvaluator.TryParseSourceTime(
            "09/13/2026 05:15 PM",
            TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"),
            out var utc);

        Assert.True(parsed);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 0, 15, 0, TimeSpan.Zero), utc);
    }

    [Theory]
    [InlineData("", "as_of_missing")]
    [InlineData(", \"asOf\": null", "as_of_missing")]
    [InlineData(", \"asOf\": \"   \"", "as_of_empty")]
    [InlineData(", \"asOf\": 123", "as_of_invalid")]
    public async Task Read_AsOfMetadataIsInvalid_ReturnsStableErrorCode(string asOfProperty, string expectedCode)
    {
        using var tmp = new TempDir();
        var gradesPath = Path.Combine(tmp.Path, "currentGrades.json");
        File.WriteAllText(gradesPath, $$"""
        {
          "userName": "Vlad"{{asOfProperty}},
          "currentGrades": []
        }
        """);
        var settings = TestRuntimeFactory.Create(
            Path.Combine(tmp.Path, "state.db"),
            Path.Combine(tmp.Path, "kid-access-config.json"),
            gradesPath);

        var result = await new CurrentGradesFileReader(settings).ReadAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    private static GradeLimitPolicyEvaluator CreateEvaluator(
        TempDir tmp,
        DateTimeOffset? sourceTime = null,
        double averagePercentage = 0.95,
        bool includeNullGrade = false)
    {
        if (sourceTime is not null)
        {
            WriteGrades(tmp, sourceTime.Value.ToString("O"), averagePercentage, includeNullGrade);
        }

        var limitsPath = Path.Combine(tmp.Path, "kid-access-config.json");
        var gradesPath = Path.Combine(tmp.Path, "currentGrades.json");
        var settings = TestRuntimeFactory.Create(Path.Combine(tmp.Path, "state.db"), limitsPath, gradesPath);
        return new GradeLimitPolicyEvaluator(
            new CurrentGradesFileReader(settings),
            NullLogger<GradeLimitPolicyEvaluator>.Instance);
    }

    private static void WriteGrades(
        TempDir tmp,
        string asOf,
        double averagePercentage,
        bool includeNullGrade = false)
    {
        var nullGrade = includeNullGrade
            ? """, { "classId": "Homeroom", "averagePercentage": null, "mark": null, "missing": 0, "lastUpdated": null }"""
            : string.Empty;
        File.WriteAllText(Path.Combine(tmp.Path, "currentGrades.json"), $$"""
        {
          "userName": " vLaD ",
          "asOf": "{{asOf}}",
          "currentGrades": [
            { "classId": "Math", "averagePercentage": {{averagePercentage.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "mark": "B", "missing": 0, "lastUpdated": null }{{nullGrade}}
          ]
        }
        """);
    }

    private static UserLimitConfig CreateUser(
        double threshold = 0.85,
        int restrictedLimitMinutes = 300,
        int normalDecisionTtlMinutes = 20_160)
        => new(
            "Vlad",
            "Vlad",
            null,
            [],
            120,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mon"] = 600 },
            new GradeLimitPolicyConfig(true, threshold, restrictedLimitMinutes, normalDecisionTtlMinutes));

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kid-control-policy-tests-{Guid.NewGuid():N}");
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
