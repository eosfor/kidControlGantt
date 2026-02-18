public sealed class SessionRepositoryTests
{
    [Fact]
    public void SessionLifecycle_TracksUsageAndCompletion()
    {
        using var tmp = new TempDir();
        var repo = CreateRepository(tmp.Path);

        const string userName = "kid-1";
        const long startedAtMs = 1_000_000;
        const long expiresAtMs = 1_120_000;

        repo.InsertSession(userName, startedAtMs, expiresAtMs, startedAtMs);
        var active = repo.GetActiveSession(userName);
        Assert.NotNull(active);

        repo.ExtendSession(active!.Id, 1_180_000, 1_010_000);
        var extended = repo.GetActiveSession(userName);
        Assert.NotNull(extended);
        Assert.Equal(1_180_000, extended!.ExpiresAtMs);

        var ended = repo.EndSession(extended.Id, 1_090_000, "manual", 1_090_000);
        Assert.True(ended);
        Assert.Null(repo.GetActiveSession(userName));

        var usageMs = repo.SumCompletedUsageMs(userName, 0, 2_000_000);
        Assert.Equal(90_000, usageMs);
    }

    [Fact]
    public void SoonEndingCandidates_AreReturnedAndMarkedOnce()
    {
        using var tmp = new TempDir();
        var repo = CreateRepository(tmp.Path);

        const string userName = "kid-1";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        repo.InsertSession(userName, nowMs - 30_000, nowMs + (5 * 60_000), nowMs);
        repo.InsertSession("kid-2", nowMs - 30_000, nowMs + (30 * 60_000), nowMs);

        var soon = repo.ListSoonEndingCandidates(nowMs, nowMs + (10 * 60_000));
        Assert.Single(soon);

        var marked = repo.MarkSoonEndingNotified(soon[0].Id, nowMs);
        Assert.True(marked);
        Assert.False(repo.MarkSoonEndingNotified(soon[0].Id, nowMs));

        var afterMark = repo.ListSoonEndingCandidates(nowMs, nowMs + (10 * 60_000));
        Assert.Empty(afterMark);
    }

    private static SessionRepository CreateRepository(string tempDir)
    {
        var settings = TestRuntimeFactory.Create(
            dbPath: System.IO.Path.Combine(tempDir, "state.db"),
            limitsConfigPath: System.IO.Path.Combine(tempDir, "config.json"));
        return new SessionRepository(settings);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kid-control-tests-{Guid.NewGuid():N}");
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
                // no-op in tests; temp cleanup is best-effort
            }
        }
    }
}
