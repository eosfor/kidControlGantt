using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class RouterReconciliationTests
{
    [Fact]
    public async Task EmptyDatabase_ClearsAllWindowsAndPausesOnlyManagedUser()
    {
        using var f = new Fixture();
        f.Router.Managed["mon"] = "0s-1d";
        f.Router.Managed["tur-sun"] = "15h-21h";
        var unrelated = JsonSerializer.Serialize(f.Router.Unmanaged);

        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);

        f.AssertBlocked();
        Assert.Equal(unrelated, JsonSerializer.Serialize(f.Router.Unmanaged));
        Assert.All(f.Router.Writes, w => Assert.EndsWith("*1", w.Target));
        var writes = f.Router.Writes.Count;
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.Equal(writes, f.Router.Writes.Count);
    }

    [Theory]
    [InlineData("11h-13h")]
    [InlineData("11:00:00-13:00:00")]
    [InlineData("11:00-13:00")]
    public async Task IntactDatabase_ValidSessionSurvivesRestartWithoutRouterWrites(string window)
    {
        using var f = new Fixture();
        var session = f.SeedSession();
        f.Router.Managed["tue"] = window;
        f.Router.Managed["mon"] = "0s-0s";
        f.Reopen();

        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);

        Assert.Equal(session, f.Repo.GetActiveSession("child"));
        Assert.Empty(f.Router.Writes);
        Assert.False(f.Repo.HasPendingRouterSync("child"));
    }

    [Fact]
    public async Task OldDatabaseSnapshot_OverridesLongerRouterWindowAndOtherWeekdays()
    {
        using var f = new Fixture();
        var authorized = f.SeedSession();
        f.Router.Managed["tue"] = "11h-23h";
        f.Router.Managed["wed"] = "0s-1d";
        f.Router.Managed["tur-tue"] = "0s-1d";
        f.Router.Managed["disabled"] = "true";
        f.Reopen();

        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);

        Assert.Equal(authorized, f.Repo.GetActiveSession("child"));
        Assert.Equal("11h-13h", f.Router.Managed["tue"]);
        Assert.Equal("", f.Router.Managed["wed"]);
        Assert.Equal("", f.Router.Managed["tur-tue"]);
        Assert.Equal("false", f.Router.Managed["disabled"]);
        Assert.Equal(new[] { "pause", "patch", "resume" }, f.Router.Writes.Select(w => w.Kind));
    }

    [Fact]
    public async Task RecreatedDatabase_ClearsRouterStateDespitePreviouslyValidSession()
    {
        using var f = new Fixture();
        f.SeedSession();
        f.Router.Managed["tue"] = "11h-13h";
        f.RecreateDatabase();

        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);

        f.AssertBlocked();
        Assert.Null(f.Repo.GetActiveSession("child"));
    }

    [Theory]
    [InlineData("pause", false)]
    [InlineData("pause", true)]
    [InlineData("patch", false)]
    [InlineData("patch", true)]
    [InlineData("resume", false)]
    [InlineData("resume", true)]
    [InlineData("verify", false)]
    [InlineData("final", false)]
    public async Task GrantFailure_RetainsDurableIntentAndRecoversOnRestart(string failure, bool applyBeforeFailure)
    {
        using var f = new Fixture();
        f.Router.Fail = failure;
        f.Router.ApplyBeforeFailure = applyBeforeFailure;
        f.Router.BeforeWrite = () =>
        {
            Assert.NotNull(f.Repo.GetActiveSession("child"));
            Assert.True(f.Repo.HasPendingRouterSync("child"));
        };

        await Assert.ThrowsAsync<AppHttpException>(() => f.Service.RequestAccessAsync("child", 120, CancellationToken.None));

        var desired = f.Repo.GetActiveSession("child");
        Assert.NotNull(desired);
        Assert.True(f.Repo.HasPendingRouterSync("child"));
        f.Router.BeforeWrite = null;
        f.Router.Fail = null;
        f.Reopen();
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);

        Assert.Equal(desired, f.Repo.GetActiveSession("child"));
        Assert.Equal("12h-14h", f.Router.Managed["tue"]);
        Assert.Equal("false", f.Router.Managed["paused"]);
        Assert.False(f.Repo.HasPendingRouterSync("child"));
    }

    [Theory]
    [InlineData("get", false)]
    [InlineData("pause", false)]
    [InlineData("pause", true)]
    [InlineData("patch", false)]
    [InlineData("patch", true)]
    [InlineData("verify", false)]
    [InlineData("final", false)]
    public async Task DisableFailure_NeverResurrectsEndedSessionAndRetriesAfterRestart(string failure, bool applyBeforeFailure)
    {
        using var f = new Fixture();
        f.SeedSession();
        f.Router.Managed["tue"] = "11h-13h";
        f.Router.Fail = failure;
        f.Router.ApplyBeforeFailure = applyBeforeFailure;

        await Assert.ThrowsAsync<AppHttpException>(() => f.Service.DisableAccessAsync("child", CancellationToken.None));

        Assert.Null(f.Repo.GetActiveSession("child"));
        Assert.True(f.Repo.HasPendingRouterSync("child"));
        f.Router.Fail = null;
        f.Reopen();
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        f.AssertBlocked();
        Assert.DoesNotContain(f.Router.Writes, w => w.Kind == "resume");
    }

    [Fact]
    public async Task ExpirationFailure_RetriesWithoutDuplicatingExpirationAudit()
    {
        using var f = new Fixture();
        f.Repo.InsertSession("child", f.Now - 120_000, f.Now - 60_000, f.Now);
        f.Router.Managed["tue"] = "11h58m-11h59m";
        f.Router.Fail = "patch";
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.True(f.Repo.HasPendingRouterSync("child"));
        Assert.Null(f.Repo.GetActiveSession("child"));

        f.Router.Fail = null;
        f.Reopen();
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);

        f.AssertBlocked();
        var audit = f.Repo.ListAuditEventsIntersectingRange("child", f.Now - 600_000, f.Now + 1, 10);
        Assert.Single(audit, a => a.Action == "expired");
    }

    [Fact]
    public async Task PendingGrantThatExpiredWhileOffline_IsNeverResumed()
    {
        using var f = new Fixture();
        f.SeedSession();
        f.Clock.Now = f.Clock.Now.AddHours(2);
        f.Reopen();
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        f.AssertBlocked();
        Assert.DoesNotContain(f.Router.Writes, w => w.Kind == "resume");
    }

    [Fact]
    public async Task SessionExpiringDuringPatch_IsClearedWithoutResume()
    {
        using var f = new Fixture();
        f.SeedSession();
        f.Router.AfterPatch = () => f.Clock.Now = f.Clock.Now.AddHours(2);
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        f.AssertBlocked();
        Assert.DoesNotContain(f.Router.Writes, w => w.Kind == "resume");
    }

    [Fact]
    public async Task ExtensionAndShortening_ReplaceRouterWindowUsingOriginalSessionStart()
    {
        using var f = new Fixture();
        var original = f.SeedSession();
        await f.Service.RequestAccessAsync("child", 180, CancellationToken.None);
        Assert.Equal("11h-14h", f.Router.Managed["tue"]);
        await f.Service.RequestAccessAsync("child", 90, CancellationToken.None);
        Assert.Equal("11h-12h30m", f.Router.Managed["tue"]);
        Assert.Equal(original.Id, f.Repo.GetActiveSession("child")?.Id);
        Assert.False(f.Repo.HasPendingRouterSync("child"));
    }

    [Fact]
    public async Task SweepAndDisable_WaitForInFlightGrantThenUseLatestDatabaseState()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Router.BlockPatch = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var grant = f.Service.RequestAccessAsync("child", 120, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var sweep = f.Service.SweepExpiredSessionsAsync(timeout.Token);
        var disable = f.Service.DisableAccessAsync("child", timeout.Token);
        Assert.False(sweep.IsCompleted);
        Assert.False(disable.IsCompleted);
        Assert.Single(f.Router.Writes); // pause only; PATCH is held before application
        release.SetResult();
        await Task.WhenAll(grant, sweep, disable);
        f.AssertBlocked();
        Assert.Null(f.Repo.GetActiveSession("child"));
    }

    [Fact]
    public async Task CancellationAfterPause_LeavesPendingIntentAndReleasesUserGate()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Router.BlockPatch = ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.RequestAccessAsync("child", 120, cts.Token));
        Assert.True(f.Repo.HasPendingRouterSync("child"));
        Assert.Equal("true", f.Router.Managed["paused"]);
        f.Router.BlockPatch = null;
        await f.Service.DisableAccessAsync("child", CancellationToken.None);
        f.AssertBlocked();
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("extend")]
    [InlineData("disable")]
    public async Task DatabaseFailure_RollsBackSessionAndDoesNotMutateRouter(string operation)
    {
        using var f = new Fixture();
        var original = operation == "insert" ? null : f.SeedSession();
        f.Repo.CompleteRouterSync("child");
        using (var conn = new SqliteConnection($"Data Source={f.Settings.DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_pending BEFORE INSERT ON pending_router_sync BEGIN SELECT RAISE(ABORT, 'injected persistence failure'); END";
            cmd.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => operation == "disable"
            ? (Task)f.Service.DisableAccessAsync("child", CancellationToken.None)
            : f.Service.RequestAccessAsync("child", 120, CancellationToken.None));
        Assert.Equal(original, f.Repo.GetActiveSession("child"));
        Assert.Empty(f.Router.Writes);
        Assert.False(f.Repo.HasPendingRouterSync("child"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task MissingOrAmbiguousManagedEntry_RetainsPendingWorkWithoutTouchingOtherProfiles(int count)
    {
        using var f = new Fixture();
        f.SeedSession();
        f.Router.ManagedEntryCount = count;
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.True(f.Repo.HasPendingRouterSync("child"));
        Assert.Empty(f.Router.Writes);
    }

    [Fact]
    public async Task RouterIgnoringPatch_IsNotReportedAsSynchronized()
    {
        using var f = new Fixture();
        f.Router.IgnorePatch = true;
        f.Router.Managed["sun"] = "0s-1d";
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.True(f.Repo.HasPendingRouterSync("child"));
        Assert.Equal("true", f.Router.Managed["paused"]);
    }

    [Fact]
    public async Task RouterIgnoringNarrowerSchedule_IsNeverResumed()
    {
        using var f = new Fixture();
        f.SeedSession();
        f.Router.Managed["tue"] = "0s-1d";
        f.Router.IgnorePatch = true;

        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);

        Assert.True(f.Repo.HasPendingRouterSync("child"));
        Assert.Equal("true", f.Router.Managed["paused"]);
        Assert.DoesNotContain(f.Router.Writes, w => w.Kind == "resume");
    }

    [Fact]
    public async Task LegacyDatabaseWithoutPendingTable_IsMigratedAndSessionPreserved()
    {
        using var f = new Fixture();
        var original = f.SeedSession();
        f.Router.Managed["tue"] = "11h-13h";
        using (var conn = new SqliteConnection($"Data Source={f.Settings.DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE pending_router_sync";
            cmd.ExecuteNonQuery();
        }
        f.Reopen();
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.Equal(original, f.Repo.GetActiveSession("child"));
        Assert.False(f.Repo.HasPendingRouterSync("child"));
        Assert.Empty(f.Router.Writes);
    }

    [Fact]
    public async Task ExtensionFailure_AppliesPersistedEndOnRestart()
    {
        using var f = new Fixture();
        var original = f.SeedSession();
        f.Router.Managed["tue"] = "11h-13h";
        f.Router.Fail = "patch";
        await Assert.ThrowsAsync<AppHttpException>(() => f.Service.RequestAccessAsync("child", 180, CancellationToken.None));
        Assert.Equal(original.Id, f.Repo.GetActiveSession("child")?.Id);
        Assert.Equal(f.Now + 7_200_000, f.Repo.GetActiveSession("child")?.ExpiresAtMs);
        Assert.Equal("true", f.Router.Managed["paused"]);

        f.Router.Fail = null;
        f.Reopen();
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.Equal("11h-14h", f.Router.Managed["tue"]);
        Assert.Equal("false", f.Router.Managed["paused"]);
    }

    [Fact]
    public async Task FutureSession_IsBlockedRatherThanGrantingWeeklyAccessEarly()
    {
        using var f = new Fixture();
        f.Repo.InsertSession("child", f.Now + 3_600_000, f.Now + 7_200_000, f.Now);
        f.Router.Managed["tue"] = "0s-1d";
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.True(f.Repo.HasPendingRouterSync("child"));
        Assert.Equal("true", f.Router.Managed["paused"]);
        Assert.Equal("", f.Router.Managed["tue"]);
        Assert.DoesNotContain(f.Router.Writes, w => w.Kind == "resume");
    }

    [Fact]
    public async Task DatabaseFailureAfterResume_RecoversCompletionWithoutReapplyingGrant()
    {
        using var f = new Fixture();
        using var conn = new SqliteConnection($"Data Source={f.Settings.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TRIGGER fail_completion BEFORE DELETE ON pending_router_sync BEGIN SELECT RAISE(ABORT, 'injected completion failure'); END";
        cmd.ExecuteNonQuery();
        await Assert.ThrowsAsync<SqliteException>(() => f.Service.RequestAccessAsync("child", 120, CancellationToken.None));
        Assert.Equal("false", f.Router.Managed["paused"]);
        Assert.Equal("12h-14h", f.Router.Managed["tue"]);
        Assert.True(f.Repo.HasPendingRouterSync("child"));
        var writes = f.Router.Writes.Count;
        cmd.CommandText = "DROP TRIGGER fail_completion";
        cmd.ExecuteNonQuery();
        f.Reopen();
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.Equal(writes, f.Router.Writes.Count);
        Assert.False(f.Repo.HasPendingRouterSync("child"));
    }

    [Fact]
    public async Task LowGrades_DoNotShortenAlreadyAuthorizedSessionDuringReconciliation()
    {
        using var f = new Fixture();
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f.Settings.LimitsConfigPath))!;
        config["users"]![0]!["gradeLimitPolicy"] = System.Text.Json.Nodes.JsonNode.Parse("""
            {"enabled":true,"threshold":0.85,"restrictedLimitMinutes":300,"normalDecisionTtlMinutes":20160}
            """);
        File.WriteAllText(f.Settings.LimitsConfigPath, config.ToJsonString());
        File.WriteAllText(f.Settings.CurrentGradesPath, """
            {"userName":"child","asOf":"2026-10-27T12:00:00Z","currentGrades":[
                {"classId":"Math","averagePercentage":0.5,"mark":"F","missing":0,"lastUpdated":null}]}
            """);
        f.Repo.InsertSession("child", f.Now - 3_600_000, f.Now + 18_000_000, f.Now);
        f.Reopen();
        await f.Service.SweepExpiredSessionsAsync(CancellationToken.None);
        Assert.Equal("11h-17h", f.Router.Managed["tue"]);
        Assert.Equal(f.Now + 18_000_000, f.Repo.GetActiveSession("child")?.ExpiresAtMs);
        Assert.Equal("false", f.Router.Managed["paused"]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kid-reconcile-{Guid.NewGuid():N}");
        private readonly HttpClient _http;
        public FixedClock Clock { get; } = new();
        public long Now => Clock.GetUtcNow().ToUnixTimeMilliseconds();
        public RouterHandler Router { get; } = new();
        public RuntimeSettings Settings { get; private set; }
        public SessionRepository Repo { get; private set; }
        public KidControlService Service { get; private set; }

        public Fixture()
        {
            Directory.CreateDirectory(_dir);
            var config = Path.Combine(_dir, "config.json");
            File.WriteAllText(config, """
                {"timezone":"UTC","endingSoonMinutes":0,"users":[{"name":"child","limitsMinutes":{
                    "mon":600,"tue":600,"wed":600,"thu":600,"fri":600,"sat":600,"sun":600}}]}
                """);
            Settings = TestRuntimeFactory.Create(Path.Combine(_dir, "state.db"), config);
            Repo = new SessionRepository(Settings);
            _http = new HttpClient(Router);
            Service = CreateService();
        }
        private KidControlService CreateService() => new(
            new AccessConfigProvider(Settings),
            new GradeLimitPolicyEvaluator(new CurrentGradesFileReader(Settings), NullLogger<GradeLimitPolicyEvaluator>.Instance),
            Repo, new MikrotikClient(_http, Settings, Clock),
            new EmailNotificationService(Settings, NullLogger<EmailNotificationService>.Instance),
            NullLogger<KidControlService>.Instance, Clock);
        public SessionRecord SeedSession()
        {
            Repo.InsertSession("child", Now - 3_600_000, Now + 3_600_000, Now);
            return Repo.GetActiveSession("child")!;
        }
        public void Reopen() { Repo = new SessionRepository(Settings); Service = CreateService(); }
        public void RecreateDatabase()
        {
            Settings = TestRuntimeFactory.Create(Path.Combine(_dir, "recreated.db"), Settings.LimitsConfigPath);
            Reopen();
        }
        public void AssertBlocked()
        {
            Assert.Equal("true", Router.Managed["paused"]);
            Assert.Equal("false", Router.Managed["disabled"]);
            Assert.All(Router.Managed.Where(p => Days.Contains(p.Key) || p.Key.StartsWith("tur-", StringComparison.Ordinal)), p => Assert.Equal("", p.Value));
            Assert.False(Repo.HasPendingRouterSync("child"));
        }
        public void Dispose() { _http.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private sealed class RouterHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Managed { get; } = Entry("*1", "child");
        public Dictionary<string, string> Unmanaged { get; } = Entry("*2", "manual");
        public List<(string Kind, string Target)> Writes { get; } = [];
        public string? Fail;
        public bool ApplyBeforeFailure, IgnorePatch;
        public int ManagedEntryCount = 1;
        public Action? BeforeWrite, AfterPatch;
        public Func<CancellationToken, Task>? BlockPatch;
        private int _readsAfterWrite;
        private static Dictionary<string, string> Entry(string id, string name)
        {
            var entry = new Dictionary<string, string> { [".id"] = id, ["name"] = name, ["paused"] = "false", ["disabled"] = "false" };
            foreach (var day in Days) { entry[day] = ""; entry[$"tur-{day}"] = ""; }
            return entry;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var kind = request.Method == HttpMethod.Get ? "get" : request.Method == HttpMethod.Patch ? "patch"
                : request.RequestUri!.Segments.Last();
            if (kind == "get" && Writes.Count > 0) _readsAfterWrite++;
            var fail = Fail == kind || Fail == "verify" && kind == "get" && Writes.Count > 0
                || Fail == "final" && kind == "get" && _readsAfterWrite >= 2;
            if (kind == "get")
            {
                return Response(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
                    JsonSerializer.Serialize(Enumerable.Repeat(Managed, ManagedEntryCount).Append(Unmanaged)));
            }
            BeforeWrite?.Invoke();
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            var target = kind == "patch" ? request.RequestUri!.Segments.Last() : json.RootElement.GetProperty("numbers").GetString()!;
            Assert.Equal("*1", target);
            if (kind == "patch" && BlockPatch is not null) await BlockPatch(ct);
            Writes.Add((kind, target));
            if (!fail || ApplyBeforeFailure)
            {
                if (kind == "pause") Managed["paused"] = "true";
                else if (kind == "resume") Managed["paused"] = "false";
                else if (!IgnorePatch)
                {
                    foreach (var property in json.RootElement.EnumerateObject()) Managed[property.Name] = property.Value.GetString()!;
                }
                if (kind == "patch") AfterPatch?.Invoke();
            }
            return Response(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "{}");
        }
        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
