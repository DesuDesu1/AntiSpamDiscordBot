using AntiSpam.Bot.Data;
using AntiSpam.Bot.Domain.GuildManagement;
using AntiSpam.Bot.Domain.SpamDetection;
using AntiSpam.Bot.Domain.SpamIncident;
using AntiSpam.Bot.Infrastructure.Cache;
using AntiSpam.Bot.Infrastructure.Discord;
using AntiSpam.Contracts.Events;
using Mediator;
using ZiggyCreatures.Caching.Fusion;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.SpamDetection;

public sealed record DetectSpamCommand(MessageReceivedEvent Message) : ICommand;

public sealed class DetectSpamHandler : ICommandHandler<DetectSpamCommand>
{
    private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromHours(1);

    private readonly BotDbContext _db;
    private readonly IFusionCache _cache;
    private readonly MessageRepository _messageRepository;
    private readonly DiscordService _discord;
    private readonly ILogger<DetectSpamHandler> _logger;

    public DetectSpamHandler(BotDbContext db,
        IFusionCache cache,
        MessageRepository messageRepository,
        DiscordService discord,
        ILogger<DetectSpamHandler> logger)
    {
        _db = db;
        _cache = cache;
        _messageRepository = messageRepository;
        _discord = discord;
        _logger = logger;
    }

    public async ValueTask<Unit> Handle(DetectSpamCommand command, CancellationToken ct)
    {
        var message = command.Message;
        var config = await LoadConfigAsync(message.GuildId, ct);
        if (!config.IsEnabled
            || config.DetectNewUserLinks
            && await TryHandleSuspiciousNewUserLinkAsync(message, config)
            || (string.IsNullOrWhiteSpace(message.Content)
                && message.AttachmentCount == 0))
        {
            return Unit.Value;
        }

        await CheckSpamWindowAsync(message, config);
        return Unit.Value;
    }

    private async Task<GuildConfig> LoadConfigAsync(ulong guildId, CancellationToken ct)
    {
        var snapshot = await _cache.GetOrSetAsync(
            CacheKeys.GuildConfig(guildId),
            async token =>
            {
                var config = await _db.GuildConfigs.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.GuildId == guildId, token);
                if (config == null)
                {
                    config = GuildConfig.CreateDefault(guildId);
                    _db.GuildConfigs.Add(config);
                    await _db.SaveChangesAsync(token);
                }

                return config.ToSnapshot();
            },
            ConfigCacheTtl,
            ct);

        return GuildConfig.FromSnapshot(snapshot);
    }

    private async Task<bool> TryHandleSuspiciousNewUserLinkAsync(MessageReceivedEvent message, GuildConfig config)
    {
        var verdict = LinkPolicy.Evaluate(message.Content, message.GuildId, config);
        if (verdict is LinkVerdict.NoLinks or LinkVerdict.Allowed)
            return false;

        var threshold = TimeSpan.FromHours(config.NewUserHoursThreshold);
        var joinedAt = message.AuthorJoinedAt ?? await _discord.GetUserJoinedAtAsync(message.GuildId, message.AuthorId);
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

        _logger.LogWarning(
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
            var resolved = await _discord.ResolveInviteAsync(code);
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
        if (!await _messageRepository.TryClaimActionAsync(message.GuildId, message.AuthorId, cooldown))
        {
            if (config.DeleteMessages)
                await _discord.BulkDeleteMessagesAsync(message.GuildId, [(message.ChannelId, message.MessageId)]);
            return;
        }

        var memberForDisplay = memberFor.HasValue ? FormatDuration(memberFor.Value) : "unknown";
        var incident = SpamIncident.Raise(
            message.GuildId, message.AuthorId, message.AuthorUsername,
            $"[NEW USER - joined {memberForDisplay} ago] {message.Content}", [message.ChannelId]);
        await PersistIncidentAsync(incident);

        var muteApplied = config.MuteOnSpam
            ? (bool?)await _discord.MuteUserAsync(message.GuildId, message.AuthorId, TimeSpan.FromMinutes(config.MuteDurationMinutes))
            : null;

        if (config.AlertChannelId.HasValue)
        {
            await _discord.SendNewUserLinkAlertAsync(
                message.GuildId, config.AlertChannelId.Value, incident, memberFor, config, message.AttachmentUrls, muteApplied);
        }

        if (config.DeleteMessages)
        {
            await _discord.BulkDeleteMessagesAsync(message.GuildId, [(message.ChannelId, message.MessageId)]);
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

        var recentMessages = await _messageRepository.GetInWindowAsync(message.GuildId, message.AuthorId, options.Window);
        var newMessage = new CachedMessage(
            message.Content, message.ChannelId, message.MessageId,
            message.Timestamp.ToUnixTimeSeconds(), message.AttachmentCount);
        await _messageRepository.AddAsync(message.GuildId, message.AuthorId, newMessage, options.Window);

        var verdict = new MessageWindow(recentMessages).Evaluate(newMessage, options);
        if (!verdict.IsSpam)
            return;

        _logger.LogWarning(
            "SPAM DETECTED: User {User} ({Id}) - {Channels} channels, reason: {Reason}, similarity: {Similarity:P0}",
            message.AuthorUsername, message.AuthorId, verdict.ChannelCount, verdict.Reason, verdict.MaxSimilarity);

        await RaiseSpamIncidentAsync(message, config, verdict);
    }

    private async Task RaiseSpamIncidentAsync(MessageReceivedEvent message, GuildConfig config, SpamVerdict verdict)
    {
        var cooldown = TimeSpan.FromSeconds(config.DetectionWindowSeconds);
        if (!await _messageRepository.TryClaimActionAsync(message.GuildId, message.AuthorId, cooldown))
        {
            if (config.DeleteMessages)
                await _discord.BulkDeleteMessagesAsync(message.GuildId, [(message.ChannelId, message.MessageId)]);
            return;
        }

        var incident = SpamIncident.Raise(
            message.GuildId, message.AuthorId, message.AuthorUsername, message.Content, verdict.ChannelIds);
        await PersistIncidentAsync(incident);

        var muteApplied = config.MuteOnSpam
            ? (bool?)await _discord.MuteUserAsync(message.GuildId, message.AuthorId, TimeSpan.FromMinutes(config.MuteDurationMinutes))
            : null;

        if (config.AlertChannelId.HasValue)
        {
            await _discord.SendAlertAsync(message.GuildId, config.AlertChannelId.Value, incident, config, message.AttachmentUrls, muteApplied);
        }

        if (config.DeleteMessages)
        {
            var messagesToDelete = verdict.MatchingMessages.Select(m => (m.ChannelId, m.MessageId)).ToList();
            await _discord.BulkDeleteMessagesAsync(message.GuildId, messagesToDelete);
        }
    }

    private async Task PersistIncidentAsync(SpamIncident incident)
    {
        _db.SpamIncidents.Add(incident);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Created spam incident #{Id} for user {User} in guild {Guild}",
            incident.Id, incident.Username, incident.GuildId);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 60)
            return $"{(int)duration.TotalMinutes}m";
        return duration.TotalHours < 24 
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m" 
            : $"{(int)duration.TotalDays}d {duration.Hours}h";
    }
}
