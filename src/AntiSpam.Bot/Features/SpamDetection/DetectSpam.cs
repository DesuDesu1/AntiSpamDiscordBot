using AntiSpam.Bot.Data;
using AntiSpam.Bot.Domain.GuildManagement;
using AntiSpam.Bot.Domain.SpamDetection;
using AntiSpam.Bot.Domain.SpamIncident;
using AntiSpam.Bot.Infrastructure.Cache;
using AntiSpam.Bot.Infrastructure.Discord;
using AntiSpam.Contracts.Events;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.SpamDetection;

public sealed record DetectSpamCommand(MessageReceivedEvent Message) : ICommand;

/// <summary>
/// Runs both spam checks (cross-channel repost, new-user link) for one incoming message and
/// carries out the resulting moderation action. Scoring is delegated to the pure
/// <see cref="MessageWindow"/>/<see cref="LinkPolicy"/> domain types; this handler is orchestration
/// only (Redis window, Discord actions, incident persistence). Guild config is read through the
/// Redis cache - this is the hottest read path in the bot (every message hits it).
/// </summary>
public sealed class DetectSpamHandler(
    BotDbContext db,
    GuildConfigCache configCache,
    MessageRepository messageRepository,
    DiscordService discord,
    ILogger<DetectSpamHandler> logger) : ICommandHandler<DetectSpamCommand>
{
    public async ValueTask<Unit> Handle(DetectSpamCommand command, CancellationToken ct)
    {
        var message = command.Message;
        var config = await LoadConfigAsync(message.GuildId, ct);
        if (!config.IsEnabled)
            return Unit.Value;

        if (config.DetectNewUserLinks && await TryHandleSuspiciousNewUserLinkAsync(message, config))
            return Unit.Value;

        if (string.IsNullOrWhiteSpace(message.Content) && message.AttachmentCount == 0)
            return Unit.Value;

        await CheckSpamWindowAsync(message, config);
        return Unit.Value;
    }

    /// <summary>Read-through cache: Redis first, then Postgres, seeding a default config for a first-seen guild.</summary>
    private async Task<GuildConfig> LoadConfigAsync(ulong guildId, CancellationToken ct)
    {
        var cached = await configCache.TryGetAsync(guildId);
        if (cached != null)
            return cached;

        var config = await db.GuildConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId, ct);
        if (config == null)
        {
            config = GuildConfig.CreateDefault(guildId);
            db.GuildConfigs.Add(config);
            await db.SaveChangesAsync(ct);
        }

        await configCache.SetAsync(config);
        return config;
    }

    private async Task<bool> TryHandleSuspiciousNewUserLinkAsync(MessageReceivedEvent message, GuildConfig config)
    {
        var verdict = LinkPolicy.Evaluate(message.Content, message.GuildId, config);
        if (verdict is LinkVerdict.NoLinks or LinkVerdict.Allowed)
            return false;

        var threshold = TimeSpan.FromHours(config.NewUserHoursThreshold);
        var joinedAt = message.AuthorJoinedAt ?? await discord.GetUserJoinedAtAsync(message.GuildId, message.AuthorId);
        var (isNew, memberFor) = IsNewUser(joinedAt, threshold);
        if (!isNew)
            return false;

        // discord.gg invites still need an API round-trip to resolve their target guild before we
        // know whether they're allowed (our own server, or one on the invite allow-list).
        if (verdict == LinkVerdict.PendingInviteVerification &&
            await AllInvitesAllowedAsync(message.Content, config))
        {
            return false;
        }

        logger.LogWarning(
            "SUSPICIOUS: New user {User} ({Id}) posted link, member for {MemberFor} (threshold: {Threshold}h) in guild {Guild}",
            message.AuthorUsername, message.AuthorId, memberFor, config.NewUserHoursThreshold, message.GuildId);

        await RaiseSuspiciousNewUserIncidentAsync(message, config, memberFor);
        return true;
    }

    private async Task<bool> AllInvitesAllowedAsync(string content, GuildConfig config)
    {
        var codes = LinkPolicy.ExtractInviteCodes(content).ToList();
        if (codes.Count == 0)
            return false;

        foreach (var code in codes)
        {
            var resolved = await discord.ResolveInviteAsync(code);
            if (resolved is not { } invite || !config.IsInviteGuildAllowed(invite.GuildId))
                return false;
        }

        return true;
    }

    private static (bool IsNew, TimeSpan? MemberFor) IsNewUser(DateTimeOffset? joinedAt, TimeSpan threshold)
    {
        if (joinedAt == null)
            return (false, null);

        var memberFor = DateTimeOffset.UtcNow - joinedAt.Value;
        return (memberFor < threshold, memberFor);
    }

    private async Task RaiseSuspiciousNewUserIncidentAsync(MessageReceivedEvent message, GuildConfig config, TimeSpan? memberFor)
    {
        var cooldown = TimeSpan.FromSeconds(config.DetectionWindowSeconds);
        if (!await messageRepository.TryClaimActionAsync(message.GuildId, message.AuthorId, cooldown))
        {
            // Straggler: the new user dropped links across several channels; we alert once, but every
            // one of those messages still needs deleting.
            if (config.DeleteMessages)
                await discord.BulkDeleteMessagesAsync(message.GuildId, [(message.ChannelId, message.MessageId)]);
            return;
        }

        var memberForDisplay = memberFor.HasValue ? FormatDuration(memberFor.Value) : "unknown";
        var incident = SpamIncident.Raise(
            message.GuildId, message.AuthorId, message.AuthorUsername,
            $"[NEW USER - joined {memberForDisplay} ago] {message.Content}", [message.ChannelId]);
        await PersistIncidentAsync(incident);

        var muteApplied = config.MuteOnSpam
            ? (bool?)await discord.MuteUserAsync(message.GuildId, message.AuthorId, TimeSpan.FromMinutes(config.MuteDurationMinutes))
            : null;

        if (config.AlertChannelId.HasValue)
        {
            await discord.SendNewUserLinkAlertAsync(
                message.GuildId, config.AlertChannelId.Value, incident, memberFor, config, message.AttachmentUrls, muteApplied);
        }

        if (config.DeleteMessages)
        {
            await discord.BulkDeleteMessagesAsync(message.GuildId, [(message.ChannelId, message.MessageId)]);
        }
    }

    private async Task CheckSpamWindowAsync(MessageReceivedEvent message, GuildConfig config)
    {
        var options = new SpamDetectionOptions
        {
            MinChannels = config.MinChannelsForSpam,
            SimilarityThreshold = config.SimilarityThreshold,
            Window = TimeSpan.FromSeconds(config.DetectionWindowSeconds)
        };

        var recentMessages = await messageRepository.GetInWindowAsync(message.GuildId, message.AuthorId, options.Window);
        var newMessage = new CachedMessage(
            message.Content, message.ChannelId, message.MessageId,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), message.AttachmentCount);
        await messageRepository.AddAsync(message.GuildId, message.AuthorId, newMessage, options.Window);

        var verdict = new MessageWindow(recentMessages).Evaluate(newMessage, options);
        if (!verdict.IsSpam)
            return;

        logger.LogWarning(
            "SPAM DETECTED: User {User} ({Id}) - {Channels} channels, reason: {Reason}, similarity: {Similarity:P0}",
            message.AuthorUsername, message.AuthorId, verdict.ChannelCount, verdict.Reason, verdict.MaxSimilarity);

        await RaiseSpamIncidentAsync(message, config, verdict);
    }

    private async Task RaiseSpamIncidentAsync(MessageReceivedEvent message, GuildConfig config, SpamVerdict verdict)
    {
        var cooldown = TimeSpan.FromSeconds(config.DetectionWindowSeconds);
        if (!await messageRepository.TryClaimActionAsync(message.GuildId, message.AuthorId, cooldown))
        {
            // A straggler in a burst we've already alerted/muted on: the one-time action is done,
            // but this message still scored as spam and must be removed so it doesn't slip through
            // (the claim only collapses the alert/mute, it must not suppress deletion).
            if (config.DeleteMessages)
                await discord.BulkDeleteMessagesAsync(message.GuildId, [(message.ChannelId, message.MessageId)]);
            return;
        }

        var incident = SpamIncident.Raise(
            message.GuildId, message.AuthorId, message.AuthorUsername, message.Content, verdict.ChannelIds);
        await PersistIncidentAsync(incident);

        var muteApplied = config.MuteOnSpam
            ? (bool?)await discord.MuteUserAsync(message.GuildId, message.AuthorId, TimeSpan.FromMinutes(config.MuteDurationMinutes))
            : null;

        if (config.AlertChannelId.HasValue)
        {
            await discord.SendAlertAsync(message.GuildId, config.AlertChannelId.Value, incident, config, message.AttachmentUrls, muteApplied);
        }

        if (config.DeleteMessages)
        {
            var messagesToDelete = verdict.MatchingMessages.Select(m => (m.ChannelId, m.MessageId)).ToList();
            await discord.BulkDeleteMessagesAsync(message.GuildId, messagesToDelete);
        }
    }

    private async Task PersistIncidentAsync(SpamIncident incident)
    {
        db.SpamIncidents.Add(incident);
        await db.SaveChangesAsync();
        logger.LogInformation("Created spam incident #{Id} for user {User} in guild {Guild}",
            incident.Id, incident.Username, incident.GuildId);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 60)
            return $"{(int)duration.TotalMinutes}m";
        if (duration.TotalHours < 24)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalDays}d {duration.Hours}h";
    }
}
