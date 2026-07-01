using System.Text.RegularExpressions;
using AntiSpam.Bot.Domain.GuildManagement;

namespace AntiSpam.Bot.Domain.SpamDetection;

public enum LinkVerdict
{
    /// <summary>No URLs in the message.</summary>
    NoLinks,
    /// <summary>Every URL matched the guild's allow-list or points at this same server.</summary>
    Allowed,
    /// <summary>Contains discord.gg/discord.com/invite links whose target guild still needs an API check.</summary>
    PendingInviteVerification,
    /// <summary>Contains a URL that isn't allowed and isn't a same-server link.</summary>
    External,
}

/// <summary>
/// Decides whether a message's links are suspicious for a given guild's allow-list. Pure
/// URL parsing/matching only — resolving discord.gg invite codes to a guild id needs the
/// Discord API and stays the caller's responsibility (see <see cref="ExtractInviteCodes"/>).
/// </summary>
public static partial class LinkPolicy
{
    public static LinkVerdict Evaluate(string? content, ulong currentGuildId, GuildConfig config)
    {
        if (string.IsNullOrWhiteSpace(content))
            return LinkVerdict.NoLinks;

        var urlMatches = UrlRegex().Matches(content);
        if (urlMatches.Count == 0)
            return LinkVerdict.NoLinks;

        foreach (Match match in urlMatches)
        {
            var url = match.Value.ToLowerInvariant();
            var domain = ExtractDomain(url);
            var urlPath = ExtractUrlPath(url);

            if (config.IsLinkAllowed(urlPath, domain))
                continue;

            var channelMatch = DiscordChannelLinkRegex().Match(url);
            if (channelMatch.Success)
            {
                if (ulong.TryParse(channelMatch.Groups[1].Value, out var guildId) && guildId == currentGuildId)
                    continue;
                return LinkVerdict.External;
            }

            if (DiscordInviteRegex().IsMatch(url))
                return LinkVerdict.PendingInviteVerification;

            if (!domain.Contains("discord.com") && !domain.Contains("discordapp.com"))
                return LinkVerdict.External;
        }

        return LinkVerdict.Allowed;
    }

    public static IEnumerable<string> ExtractInviteCodes(string content) =>
        DiscordInviteRegex().Matches(content).Select(m => m.Groups[1].Value);

    /// <summary>Extracts the bare invite code from a full link, a discord.gg/x form, or a raw code.</summary>
    public static string ParseInviteCode(string input)
    {
        var code = input.Trim().TrimEnd('/');
        var lastSlash = code.LastIndexOf('/');
        if (lastSlash >= 0)
            code = code[(lastSlash + 1)..];
        var query = code.IndexOf('?');
        return query >= 0 ? code[..query] : code;
    }

    private static string ExtractDomain(string url)
    {
        if (url.StartsWith("https://")) url = url[8..];
        else if (url.StartsWith("http://")) url = url[7..];

        var slashIndex = url.IndexOf('/');
        return slashIndex > 0 ? url[..slashIndex] : url;
    }

    private static string ExtractUrlPath(string url)
    {
        if (url.StartsWith("https://")) url = url[8..];
        else if (url.StartsWith("http://")) url = url[7..];
        return url.TrimEnd('/');
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?:discord\.gg|discord\.com/invite)/([a-zA-Z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordInviteRegex();

    [GeneratedRegex(@"discord\.com/channels/(\d+)/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordChannelLinkRegex();
}
