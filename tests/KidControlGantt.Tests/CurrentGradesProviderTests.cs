public sealed class CurrentGradesProviderTests
{
    [Fact]
    public async Task GetCurrentGrades_ReturnsOnlyDashboardFieldsAndPreservesNulls()
    {
        using var tmp = new TempDir();
        var limitsPath = Path.Combine(tmp.Path, "kid-access-config.json");
        var gradesPath = Path.Combine(tmp.Path, "currentGrades.json");
        File.WriteAllText(gradesPath, """
        {
          "userName": "Vlad",
          "asOf": "09/13/2026 05:15 PM",
          "currentGrades": [
            {
              "classId": "Homeroom",
              "averagePercentage": null,
              "mark": null,
              "missing": 0,
              "teacher": "Teacher, T",
              "lastUpdated": null
            },
            {
              "classId": "AP Physics 1/2A",
              "averagePercentage": 0.8420000000000001,
              "mark": "B",
              "missing": 1,
              "lastUpdated": "09/11/2026 11:51 AM"
            }
          ]
        }
        """);

        var settings = TestRuntimeFactory.Create(Path.Combine(tmp.Path, "state.db"), limitsPath, gradesPath);
        var provider = new CurrentGradesProvider(new CurrentGradesFileReader(settings));

        var result = await provider.GetCurrentGradesAsync(CancellationToken.None);

        Assert.Equal("Vlad", result.UserName);
        Assert.Equal("09/13/2026 05:15 PM", result.AsOf);
        Assert.Equal(2, result.CurrentGrades.Count);
        Assert.Null(result.CurrentGrades[0].AveragePercentage);
        Assert.Null(result.CurrentGrades[0].Mark);
        Assert.Null(result.CurrentGrades[0].LastUpdated);
        Assert.Equal(0.8420000000000001, result.CurrentGrades[1].AveragePercentage);
        Assert.Equal(1, result.CurrentGrades[1].Missing);
    }

    [Fact]
    public async Task GetCurrentGrades_ThrowsWhenAveragePercentageIsOutsideExpectedRange()
    {
        using var tmp = new TempDir();
        var limitsPath = Path.Combine(tmp.Path, "kid-access-config.json");
        var gradesPath = Path.Combine(tmp.Path, "currentGrades.json");
        File.WriteAllText(gradesPath, """
        {
          "userName": "Vlad",
          "asOf": "09/13/2026 05:15 PM",
          "currentGrades": [
            {
              "classId": "AP Statistics A",
              "averagePercentage": 83.3,
              "mark": "B",
              "missing": 0,
              "lastUpdated": null
            }
          ]
        }
        """);

        var settings = TestRuntimeFactory.Create(Path.Combine(tmp.Path, "state.db"), limitsPath, gradesPath);
        var provider = new CurrentGradesProvider(new CurrentGradesFileReader(settings));

        var exception = await Assert.ThrowsAsync<AppHttpException>(
            () => provider.GetCurrentGradesAsync(CancellationToken.None));

        Assert.Equal(500, exception.StatusCode);
        Assert.Contains("от 0 до 1", exception.Message);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kid-control-grades-tests-{Guid.NewGuid():N}");
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
