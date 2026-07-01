using AntiSpam.Bot.Domain.GuildManagement;
using AntiSpam.Bot.Domain.SpamDetection;

namespace AntiSpam.Tests;

public class MessageWindowTests
{
    private static readonly SpamDetectionOptions Options = new() { MinChannels = 3, SimilarityThreshold = 0.7 };

    private static CachedMessage Text(string content, ulong channel) => new(content, channel, channel, 0, 0);
    private static CachedMessage Image(ulong channel) => new("", channel, channel, 0, 1);

    [Fact]
    public void Not_spam_below_min_channels()
    {
        // Same text, but only 2 distinct channels counting the new one → below MinChannels(3).
        var window = new MessageWindow([Text("buy now", 1)]);
        var verdict = window.Evaluate(Text("buy now", 2), Options);

        Assert.False(verdict.IsSpam);
        Assert.Equal(SpamReason.None, verdict.Reason);
    }

    [Fact]
    public void Similar_text_across_min_channels_is_spam()
    {
        var window = new MessageWindow([Text("buy cheap coins now", 1), Text("buy cheap coins now", 2)]);
        var verdict = window.Evaluate(Text("buy cheap coins now", 3), Options);

        Assert.True(verdict.IsSpam);
        Assert.Equal(SpamReason.SimilarText, verdict.Reason);
        Assert.Equal(3, verdict.ChannelCount);
    }

    [Fact]
    public void Distinct_text_is_not_similar_text_spam()
    {
        var window = new MessageWindow([Text("hello there friends", 1), Text("totally different words", 2)]);
        var verdict = window.Evaluate(Text("another unrelated line", 3), Options);
        Assert.False(verdict.IsSpam);
    }

    [Fact]
    public void Attachments_across_min_channels_is_attachment_spam()
    {
        var window = new MessageWindow([Image(1), Image(2)]);
        var verdict = window.Evaluate(Image(3), Options);

        Assert.True(verdict.IsSpam);
        Assert.Equal(SpamReason.AttachmentSpam, verdict.Reason);
    }

    [Fact]
    public void Same_text_repeated_in_one_channel_is_not_spam()
    {
        // Channel count is what matters, not message count.
        var window = new MessageWindow([Text("spam", 1), Text("spam", 1)]);
        var verdict = window.Evaluate(Text("spam", 1), Options);
        Assert.False(verdict.IsSpam);
    }
}

public class LinkPolicyTests
{
    private static GuildConfig Config() => GuildConfig.CreateDefault(777);

    [Fact]
    public void No_url_is_no_links()
    {
        Assert.Equal(LinkVerdict.NoLinks, LinkPolicy.Evaluate("just chatting", 777, Config()));
    }

    [Fact]
    public void Allowed_domain_is_allowed()
    {
        // youtube.com is seeded into the default allow-list.
        Assert.Equal(LinkVerdict.Allowed, LinkPolicy.Evaluate("watch https://youtube.com/watch?v=x", 777, Config()));
    }

    [Fact]
    public void Unknown_external_url_is_external()
    {
        Assert.Equal(LinkVerdict.External, LinkPolicy.Evaluate("free nitro https://evil.example/gift", 777, Config()));
    }

    [Fact]
    public void Discord_invite_needs_verification()
    {
        Assert.Equal(LinkVerdict.PendingInviteVerification, LinkPolicy.Evaluate("join https://discord.gg/abc123", 777, Config()));
    }

    [Fact]
    public void Same_guild_channel_link_is_allowed()
    {
        Assert.Equal(LinkVerdict.Allowed, LinkPolicy.Evaluate("see https://discord.com/channels/777/123", 777, Config()));
    }

    [Fact]
    public void Other_guild_channel_link_is_external()
    {
        Assert.Equal(LinkVerdict.External, LinkPolicy.Evaluate("see https://discord.com/channels/999/123", 777, Config()));
    }

    [Theory]
    [InlineData("https://discord.gg/abc123", "abc123")]
    [InlineData("discord.gg/abc123", "abc123")]
    [InlineData("discord.gg/abc123/", "abc123")]
    [InlineData("https://discord.com/invite/abc123?foo=bar", "abc123")]
    [InlineData("abc123", "abc123")]
    public void ParseInviteCode_extracts_bare_code(string input, string expected)
    {
        Assert.Equal(expected, LinkPolicy.ParseInviteCode(input));
    }
}

public class TextSimilarityTests
{
    [Fact]
    public void Identical_text_is_one()
    {
        Assert.Equal(1.0, TextSimilarity.Calculate("hello world", "hello world"), 3);
    }

    [Fact]
    public void Completely_different_text_is_low()
    {
        Assert.True(TextSimilarity.Calculate("abcdef", "zyxwvu") < 0.2);
    }

    [Fact]
    public void Zero_width_obfuscation_is_normalized_away()
    {
        // Zero-width chars inserted to dodge exact matching should not drop similarity much.
        Assert.True(TextSimilarity.Calculate("buy coins now", "buy​coins​now") > 0.7);
    }
}
