using AntiSpam.Bot.Data;
using AntiSpam.Bot.Data.Entities;
using AntiSpam.Bot.Features.GuildManagement;
using AntiSpam.Bot.Services.Cache;
using AntiSpam.Bot.Services.Discord;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.SpamDetection;

public class SpamActionService
{
    private readonly DiscordService _discord;
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly GuildConfigService _configService;
    private readonly MessageRepository _messages;
    private readonly ILogger<SpamActionService> _logger;

    public SpamActionService(
        DiscordService discord,
        IDbContextFactory<BotDbContext> dbFactory,
        GuildConfigService configService,
        MessageRepository messages,
        ILogger<SpamActionService> logger)
    {
        _discord = discord;
        _dbFactory = dbFactory;
        _configService = configService;
        _messages = messages;
        _logger = logger;
    }

    public async Task HandleSpamDetectedAsync(
        ulong guildId,
        ulong userId,
        string username,
        string content,
        List<ulong> channelIds,
        List<(ulong ChannelId, ulong MessageId)> messagesToDelete,
        IReadOnlyList<string> attachmentUrls)
    {
        var config = await _configService.GetOrCreateAsync(guildId);

        if (!config.IsEnabled)
        {
            _logger.LogDebug("Spam detection disabled for guild {GuildId}", guildId);
            return;
        }

        // Collapse a burst into one action: only the first message past the threshold acts,
        // the rest of the in-flight messages (and any redelivery) are skipped for this window.
        var cooldown = TimeSpan.FromSeconds(config.DetectionWindowSeconds);
        if (!await _messages.TryClaimActionAsync(guildId, userId, cooldown))
        {
            _logger.LogDebug("Skipping duplicate spam action for user {User} in guild {Guild} (within cooldown)", userId, guildId);
            return;
        }

        var incident = await CreateIncidentAsync(guildId, userId, username, content, channelIds);

        // Mute before alerting so the alert can report whether the mute actually landed
        // (it fails when the target outranks the bot). null = mute disabled.
        bool? muteApplied = config.MuteOnSpam
            ? await _discord.MuteUserAsync(guildId, userId, TimeSpan.FromMinutes(config.MuteDurationMinutes))
            : null;

        // Alert before deleting: the attachment image is re-hosted from its source URL,
        // which dies once the spam message is removed.
        if (config.AlertChannelId.HasValue)
        {
            await _discord.SendAlertAsync(guildId, config.AlertChannelId.Value, incident, config, attachmentUrls, muteApplied);
        }

        if (config.DeleteMessages)
        {
            await _discord.BulkDeleteMessagesAsync(guildId, messagesToDelete);
        }
    }

    /// <summary>
    /// Handles suspicious new user posting links (recently joined + link)
    /// </summary>
    public async Task HandleSuspiciousNewUserAsync(
        ulong guildId,
        ulong userId,
        string username,
        string content,
        ulong channelId,
        ulong messageId,
        TimeSpan? memberFor,
        IReadOnlyList<string> attachmentUrls)
    {
        var config = await _configService.GetOrCreateAsync(guildId);

        if (!config.IsEnabled || !config.DetectNewUserLinks)
        {
            return;
        }

        // Same burst-collapse guard: a new user dropping a link in several channels
        // should produce one alert, not one per channel.
        var cooldown = TimeSpan.FromSeconds(config.DetectionWindowSeconds);
        if (!await _messages.TryClaimActionAsync(guildId, userId, cooldown))
        {
            _logger.LogDebug("Skipping duplicate new-user-link action for user {User} in guild {Guild} (within cooldown)", userId, guildId);
            return;
        }

        var memberForDisplay = memberFor.HasValue
            ? FormatDuration(memberFor.Value) 
            : "unknown";

        // Create incident with special reason
        var incident = await CreateIncidentAsync(
            guildId, 
            userId, 
            username, 
            $"[NEW USER - joined {memberForDisplay} ago] {content}", 
            new List<ulong> { channelId });

        // Mute before alerting so the alert can report whether the mute actually landed
        // (it fails when the target outranks the bot). null = mute disabled.
        bool? muteApplied = config.MuteOnSpam
            ? await _discord.MuteUserAsync(guildId, userId, TimeSpan.FromMinutes(config.MuteDurationMinutes))
            : null;

        // Alert before deleting: the attachment image is re-hosted from its source URL,
        // which dies once the spam message is removed.
        if (config.AlertChannelId.HasValue)
        {
            await _discord.SendNewUserLinkAlertAsync(guildId, config.AlertChannelId.Value, incident, memberFor, config, attachmentUrls, muteApplied);
        }

        // Delete the message
        if (config.DeleteMessages)
        {
            await _discord.BulkDeleteMessagesAsync(guildId, new List<(ulong, ulong)> { (channelId, messageId) });
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 60)
            return $"{(int)duration.TotalMinutes}m";
        if (duration.TotalHours < 24)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalDays}d {duration.Hours}h";
    }

    private async Task<SpamIncident> CreateIncidentAsync(
        ulong guildId,
        ulong userId,
        string username,
        string content,
        List<ulong> channelIds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var incident = new SpamIncident
        {
            GuildId = guildId,
            UserId = userId,
            Username = username,
            Content = content.Length > 500 ? content[..500] : content,
            ChannelIds = channelIds,
            Status = IncidentStatus.Pending
        };

        db.SpamIncidents.Add(incident);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Created spam incident #{Id} for user {User} in guild {Guild}", 
            incident.Id, username, guildId);
        
        return incident;
    }
}
