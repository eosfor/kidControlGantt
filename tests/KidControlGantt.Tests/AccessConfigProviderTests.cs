public sealed class AccessConfigProviderTests
{
    [Fact]
    public void GetConfig_ParsesEmailsAndDeduplicatesParentList()
    {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "kid-access-config.json");
        File.WriteAllText(configPath, """
        {
          "timezone": "America/Los_Angeles",
          "defaultWindowMinutes": 90,
          "graceMinutes": 15,
          "endingSoonMinutes": 10,
          "users": [
            {
              "name": "kid-1",
              "displayName": "Kid 1",
              "email": "kid1@example.com",
              "parentEmail": ["parent@example.com", "PARENT@example.com", "second@example.com"],
              "defaultWindowMinutes": 45,
              "limitsMinutes": { "mon": 120 }
            },
            {
              "name": "kid-2",
              "displayName": "Kid 2",
              "parentEmail": "single-parent@example.com",
              "limitsMinutes": { "mon": 60 }
            }
          ]
        }
        """);

        var settings = TestRuntimeFactory.Create(
            dbPath: Path.Combine(tmp.Path, "state.db"),
            limitsConfigPath: configPath);
        var provider = new AccessConfigProvider(settings);

        var config = provider.GetConfig();

        Assert.Equal(2, config.Users.Count);
        Assert.Equal("kid1@example.com", config.Users[0].Email);
        Assert.Equal(2, config.Users[0].ParentEmails.Count);
        Assert.Contains("parent@example.com", config.Users[0].ParentEmails);
        Assert.Contains("second@example.com", config.Users[0].ParentEmails);

        Assert.Single(config.Users[1].ParentEmails);
        Assert.Equal("single-parent@example.com", config.Users[1].ParentEmails[0]);
    }

    [Fact]
    public void GetConfig_ThrowsOnInvalidDayWindow()
    {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "kid-access-config.json");
        File.WriteAllText(configPath, """
        {
          "timezone": "America/Los_Angeles",
          "dayWindows": {
            "mon": { "start": "23:00", "end": "06:00" }
          },
          "users": [
            {
              "name": "kid-1",
              "displayName": "Kid 1",
              "limitsMinutes": { "mon": 60 }
            }
          ]
        }
        """);

        var settings = TestRuntimeFactory.Create(
            dbPath: Path.Combine(tmp.Path, "state.db"),
            limitsConfigPath: configPath);
        var provider = new AccessConfigProvider(settings);

        var ex = Assert.Throws<AppHttpException>(() => provider.GetConfig());
        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("dayWindows.mon.end", ex.Message);
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
