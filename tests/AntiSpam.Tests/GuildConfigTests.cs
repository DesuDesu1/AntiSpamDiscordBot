using AntiSpam.Bot.Domain.GuildManagement;

namespace AntiSpam.Tests;

public class GuildConfigTests
{
    private static GuildConfig NewConfig() => GuildConfig.CreateDefault(123);

    [Fact]
    public void CreateDefault_seeds_well_known_links_and_defaults()
    {
        var config = NewConfig();

        Assert.True(config.IsEnabled);
        Assert.Equal(3, config.MinChannelsForSpam);
        Assert.Equal(0.7, config.SimilarityThreshold);
        Assert.Equal(120, config.DetectionWindowSeconds);
        Assert.Equal(GuildConfig.DefaultAllowedLinks, config.AllowedLinks);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void SetMinChannels_accepts_in_range(int count)
    {
        var config = NewConfig();
        config.SetMinChannels(count);
        Assert.Equal(count, config.MinChannelsForSpam);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void SetMinChannels_rejects_out_of_range(int count)
    {
        Assert.Throws<InvalidConfigValueException>(() => NewConfig().SetMinChannels(count));
    }

    [Fact]
    public void SetSimilarityThreshold_stores_percent_as_fraction()
    {
        // Regression guard: a percentage (70) must become 0.7, not be rejected as > 1.0.
        var config = NewConfig();
        config.SetSimilarityThreshold(70);
        Assert.Equal(0.7, config.SimilarityThreshold);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(101)]
    public void SetSimilarityThreshold_rejects_out_of_range(int percent)
    {
        Assert.Throws<InvalidConfigValueException>(() => NewConfig().SetSimilarityThreshold(percent));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(601)]
    public void SetDetectionWindow_rejects_out_of_range(int seconds)
    {
        Assert.Throws<InvalidConfigValueException>(() => NewConfig().SetDetectionWindow(seconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void SetMuteSettings_rejects_out_of_range_duration(int minutes)
    {
        Assert.Throws<InvalidConfigValueException>(() => NewConfig().SetMuteSettings(true, minutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(169)]
    public void SetNewUserThreshold_rejects_out_of_range(int hours)
    {
        Assert.Throws<InvalidConfigValueException>(() => NewConfig().SetNewUserThreshold(hours));
    }

    [Fact]
    public void AllowLink_normalizes_and_returns_the_stored_form()
    {
        var config = GuildConfig.CreateDefault(1);
        var stored = config.AllowLink("HTTPS://Example.com/Path/");
        Assert.Equal("example.com/path", stored);
        Assert.Contains("example.com/path", config.AllowedLinks);
    }

    [Fact]
    public void AllowLink_rejects_duplicate()
    {
        var config = GuildConfig.CreateDefault(1);
        config.AllowLink("example.com");
        Assert.Throws<LinkAlreadyAllowedException>(() => config.AllowLink("example.com"));
    }

    [Fact]
    public void RemoveLink_rejects_missing()
    {
        Assert.Throws<LinkNotAllowedException>(() => GuildConfig.CreateDefault(1).RemoveLink("not-there.com"));
    }

    [Fact]
    public void IsLinkAllowed_matches_domain_and_subdomain()
    {
        var config = GuildConfig.CreateDefault(1); // youtube.com is seeded
        Assert.True(config.IsLinkAllowed("youtube.com/watch", "youtube.com"));
        Assert.True(config.IsLinkAllowed("m.youtube.com/watch", "m.youtube.com"));
        Assert.False(config.IsLinkAllowed("evil.com", "evil.com"));
    }

    [Fact]
    public void Own_guild_invites_are_always_allowed()
    {
        Assert.True(GuildConfig.CreateDefault(42).IsInviteGuildAllowed(42));
    }

    [Fact]
    public void AllowInviteServer_allows_that_guild()
    {
        var config = GuildConfig.CreateDefault(1);
        var name = config.AllowInviteServer(999, "Partner Server");

        Assert.Equal("Partner Server", name);
        Assert.True(config.IsInviteGuildAllowed(999));
        Assert.False(config.IsInviteGuildAllowed(1000));
        Assert.Contains(config.AllowedInviteServers, s => s.GuildId == 999 && s.Name == "Partner Server");
    }

    [Fact]
    public void AllowInviteServer_rejects_own_guild()
    {
        Assert.Throws<InviteAlreadyAllowedException>(() => GuildConfig.CreateDefault(1).AllowInviteServer(1, "Self"));
    }

    [Fact]
    public void AllowInviteServer_rejects_duplicate()
    {
        var config = GuildConfig.CreateDefault(1);
        config.AllowInviteServer(999, "Partner");
        Assert.Throws<InviteAlreadyAllowedException>(() => config.AllowInviteServer(999, "Partner Again"));
    }

    [Fact]
    public void RemoveInviteServer_removes_and_returns_name()
    {
        var config = GuildConfig.CreateDefault(1);
        config.AllowInviteServer(999, "Partner");

        Assert.Equal("Partner", config.RemoveInviteServer(999));
        Assert.False(config.IsInviteGuildAllowed(999));
    }

    [Fact]
    public void RemoveInviteServer_rejects_missing()
    {
        Assert.Throws<InviteNotAllowedException>(() => GuildConfig.CreateDefault(1).RemoveInviteServer(999));
    }
}
