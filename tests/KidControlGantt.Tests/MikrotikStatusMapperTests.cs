public sealed class MikrotikStatusMapperTests
{
    [Fact]
    public void FromEntry_ReturnsUnknown_WhenEntryIsMissing()
    {
        var status = MikrotikStatusMapper.FromEntry(null);

        Assert.False(status.Active);
        Assert.False(status.Blocked);
        Assert.False(status.Paused);
        Assert.False(status.Disabled);
        Assert.Equal("unknown", status.Status);
    }

    [Fact]
    public void FromEntry_ReturnsActive_WhenNoFlagsAreSet()
    {
        var entry = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["disabled"] = "false",
            ["paused"] = "false"
        };

        var status = MikrotikStatusMapper.FromEntry(entry);

        Assert.True(status.Active);
        Assert.False(status.Blocked);
        Assert.False(status.Paused);
        Assert.False(status.Disabled);
        Assert.Equal("active", status.Status);
    }

    [Fact]
    public void FromEntry_ReturnsCombinedFlags_WhenBlockedAndPaused()
    {
        var entry = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["disabled"] = "false",
            ["paused"] = "true",
            ["blocked"] = "true"
        };

        var status = MikrotikStatusMapper.FromEntry(entry);

        Assert.False(status.Active);
        Assert.True(status.Blocked);
        Assert.True(status.Paused);
        Assert.False(status.Disabled);
        Assert.Equal("blocked+paused", status.Status);
    }

    [Fact]
    public void FromEntry_ReturnsAllFlags_WhenDisabledBlockedPaused()
    {
        var entry = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["disabled"] = "true",
            ["paused"] = "true",
            ["blocked"] = "true"
        };

        var status = MikrotikStatusMapper.FromEntry(entry);

        Assert.False(status.Active);
        Assert.True(status.Blocked);
        Assert.True(status.Paused);
        Assert.True(status.Disabled);
        Assert.Equal("blocked+paused+disabled", status.Status);
    }
}
