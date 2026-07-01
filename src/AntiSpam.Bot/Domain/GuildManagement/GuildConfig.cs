namespace AntiSpam.Bot.Domain.GuildManagement;

public sealed class GuildConfig
{
    public ulong GuildId { get; private set; }
    public ulong? AlertChannelId { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public int MinChannelsForSpam { get; private set; } = 3;
    public int DetectionWindowSeconds { get; private set; } = 120;
    public double SimilarityThreshold { get; private set; } = 0.7;
    public bool DeleteMessages { get; private set; } = true;
    public bool MuteOnSpam { get; private set; } = true;
    public int MuteDurationMinutes { get; private set; } = 60;
    public bool DetectNewUserLinks { get; private set; } = true;
    public int NewUserHoursThreshold { get; private set; } = 24;

    public static readonly string[] DefaultAllowedLinks =
    [
        // Video / streaming
        "youtube.com", "youtu.be", "twitch.tv", "vimeo.com", "dailymotion.com", "tiktok.com",
        // Images / gifs
        "tenor.com", "giphy.com", "imgur.com",
        // Music
        "spotify.com", "soundcloud.com", "music.apple.com", "bandcamp.com", "deezer.com",
        // Social
        "reddit.com", "twitter.com", "x.com", "instagram.com", "facebook.com", "fb.watch",
        "linkedin.com", "pinterest.com", "bsky.app",
        // Dev / knowledge
        "github.com", "gitlab.com", "stackoverflow.com", "wikipedia.org", "wikimedia.org", "medium.com",
        // Gaming
        "steamcommunity.com", "store.steampowered.com", "roblox.com",
    ];

    private readonly List<string> _allowedLinks = new(DefaultAllowedLinks);
    public IReadOnlyList<string> AllowedLinks => _allowedLinks;

    // External Discord servers whose invites new members may post. The bot's own guild is always
    // allowed (see IsInviteGuildAllowed); this is the explicit allow-list for OTHER servers, keyed
    // by guild id (stable, unlike invite codes which rotate/expire). The name is captured at add
    // time purely so the list is readable - the bot can't fetch it later for a server it isn't in.
    private readonly List<AllowedInviteServer> _allowedInviteServers = new();
    public IReadOnlyList<AllowedInviteServer> AllowedInviteServers => _allowedInviteServers;

    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; private set; }

    private GuildConfig()
    {
        // EF Core materialization.
    }

    public static GuildConfig CreateDefault(ulong guildId) => new() { GuildId = guildId };

    public void Enable() { IsEnabled = true; Touch(); }
    public void Disable() { IsEnabled = false; Touch(); }

    public void SetAlertChannel(ulong channelId)
    {
        AlertChannelId = channelId;
        Touch();
    }

    public void SetMinChannels(int count)
    {
        if (count is < 2 or > 10)
            throw new InvalidConfigValueException("Count must be 2-10");
        MinChannelsForSpam = count;
        Touch();
    }

    /// <param name="percent">50-100.</param>
    public void SetSimilarityThreshold(int percent)
    {
        if (percent is < 50 or > 100)
            throw new InvalidConfigValueException("Percent must be 50-100");
        SimilarityThreshold = percent / 100.0;
        Touch();
    }

    public void SetDetectionWindow(int seconds)
    {
        if (seconds is < 30 or > 600)
            throw new InvalidConfigValueException("Window must be 30-600 seconds");
        DetectionWindowSeconds = seconds;
        Touch();
    }

    public void SetMuteSettings(bool enabled, int durationMinutes)
    {
        if (durationMinutes is < 1 or > 1440)
            throw new InvalidConfigValueException("Duration must be 1-1440 minutes");
        MuteOnSpam = enabled;
        MuteDurationMinutes = durationMinutes;
        Touch();
    }

    public void SetDeleteMessages(bool enabled)
    {
        DeleteMessages = enabled;
        Touch();
    }

    /// <summary>Toggles the new-member link check independently of cross-channel spam detection.</summary>
    public void SetNewUserLinkDetection(bool enabled)
    {
        DetectNewUserLinks = enabled;
        Touch();
    }

    public void SetNewUserThreshold(int hours)
    {
        if (hours is < 1 or > 168)
            throw new InvalidConfigValueException("Hours must be 1-168 (1h to 7 days)");
        NewUserHoursThreshold = hours;
        Touch();
    }

    /// <returns>The normalized link, for the caller's confirmation message.</returns>
    public string AllowLink(string rawLink)
    {
        var link = NormalizeLink(rawLink);
        if (string.IsNullOrWhiteSpace(link))
            throw new InvalidConfigValueException("Invalid link");
        if (_allowedLinks.Contains(link, StringComparer.OrdinalIgnoreCase))
            throw new LinkAlreadyAllowedException($"`{link}` is already in allowed list");
        if (_allowedLinks.Count >= 100)
            throw new AllowedLinkLimitExceededException("Maximum 100 allowed links");

        _allowedLinks.Add(link);
        Touch();
        return link;
    }

    /// <returns>The normalized link, for the caller's confirmation message.</returns>
    public string RemoveLink(string rawLink)
    {
        var link = NormalizeLink(rawLink);
        var removed = _allowedLinks.RemoveAll(l => l.Equals(link, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            throw new LinkNotAllowedException($"`{link}` is not in allowed list");

        Touch();
        return link;
    }

    /// <summary>Adds an external server to the invite allow-list. Returns the server name for the confirmation message.</summary>
    public string AllowInviteServer(ulong inviteGuildId, string name)
    {
        if (inviteGuildId == GuildId)
            throw new InviteAlreadyAllowedException("This server's own invites are always allowed");
        if (_allowedInviteServers.Any(s => s.GuildId == inviteGuildId))
            throw new InviteAlreadyAllowedException($"**{name}** is already on the invite allow-list");
        if (_allowedInviteServers.Count >= 50)
            throw new AllowedLinkLimitExceededException("Maximum 50 allowed invite servers");

        _allowedInviteServers.Add(new AllowedInviteServer(inviteGuildId, name));
        Touch();
        return name;
    }

    /// <summary>Removes an external server from the invite allow-list. Returns the removed server's name.</summary>
    public string RemoveInviteServer(ulong inviteGuildId)
    {
        var existing = _allowedInviteServers.FirstOrDefault(s => s.GuildId == inviteGuildId)
            ?? throw new InviteNotAllowedException("That server is not on the invite allow-list");

        _allowedInviteServers.Remove(existing);
        Touch();
        return existing.Name;
    }

    /// <summary>Whether an invite pointing at <paramref name="inviteGuildId"/> is permitted: our own guild always is, plus any explicitly allow-listed server.</summary>
    public bool IsInviteGuildAllowed(ulong inviteGuildId) =>
        inviteGuildId == GuildId || _allowedInviteServers.Any(s => s.GuildId == inviteGuildId);

    /// <summary>Whether the given URL's domain or path matches one of the allowed patterns.</summary>
    public bool IsLinkAllowed(string urlPath, string domain) =>
        _allowedLinks.Any(allowed =>
            urlPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
            domain.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeLink(string link)
    {
        link = link.Trim().ToLowerInvariant();
        if (link.StartsWith("https://"))
            link = link[8..];
        else if (link.StartsWith("http://"))
            link = link[7..];
        return link.TrimEnd('/');
    }

    private void Touch() => UpdatedAt = DateTime.UtcNow;

    /// <summary>Plain-data projection for the Redis read-through cache (private setters aren't JSON-serializable).</summary>
    public GuildConfigSnapshot ToSnapshot() => new(
        GuildId, AlertChannelId, IsEnabled, MinChannelsForSpam, DetectionWindowSeconds,
        SimilarityThreshold, DeleteMessages, MuteOnSpam, MuteDurationMinutes,
        DetectNewUserLinks, NewUserHoursThreshold, _allowedLinks.ToList(),
        _allowedInviteServers.ToList(), CreatedAt, UpdatedAt);

    public static GuildConfig FromSnapshot(GuildConfigSnapshot s)
    {
        var config = new GuildConfig
        {
            GuildId = s.GuildId,
            AlertChannelId = s.AlertChannelId,
            IsEnabled = s.IsEnabled,
            MinChannelsForSpam = s.MinChannelsForSpam,
            DetectionWindowSeconds = s.DetectionWindowSeconds,
            SimilarityThreshold = s.SimilarityThreshold,
            DeleteMessages = s.DeleteMessages,
            MuteOnSpam = s.MuteOnSpam,
            MuteDurationMinutes = s.MuteDurationMinutes,
            DetectNewUserLinks = s.DetectNewUserLinks,
            NewUserHoursThreshold = s.NewUserHoursThreshold,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
        };
        config._allowedLinks.Clear();
        config._allowedLinks.AddRange(s.AllowedLinks);
        config._allowedInviteServers.AddRange(s.AllowedInviteServers);
        return config;
    }
}

/// <summary>An external Discord server allowed on a guild's invite allow-list (id is authoritative; name is a display snapshot).</summary>
public sealed record AllowedInviteServer(ulong GuildId, string Name);

public sealed record GuildConfigSnapshot(
    ulong GuildId,
    ulong? AlertChannelId,
    bool IsEnabled,
    int MinChannelsForSpam,
    int DetectionWindowSeconds,
    double SimilarityThreshold,
    bool DeleteMessages,
    bool MuteOnSpam,
    int MuteDurationMinutes,
    bool DetectNewUserLinks,
    int NewUserHoursThreshold,
    List<string> AllowedLinks,
    List<AllowedInviteServer> AllowedInviteServers,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
