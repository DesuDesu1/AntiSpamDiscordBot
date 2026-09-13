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

    private readonly IDbContextFactory<BotDbContext> _db;
    private readonly IFusionCache _cache;
    private readonly MessageRepository _messageRepository;
    private readonly DiscordService _discord;
    private readonly ILogger<DetectSpamHandler> _logger;

    public DetectSpamHandler(IDbContextFactory<BotDbContext> db,
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

    private sealed record Detection(
        string IncidentContent,
        IReadOnlyList<ulong> ChannelIds,
        List<(ulong ChannelId, ulong MessageId)> MessagesToDelete,
        TimeSpan? NewUserMemberFor);

    public async ValueTask<Unit> Handle(DetectSpamCommand command, CancellationToken ct)
    {
        var message = command.Message;

        var config = await LoadConfigAsync(message.GuildId, ct);
        if (!config.IsEnabled)
            return Unit.Value;

        var detection = config.DetectNewUserLinks
            ? await DetectNewUserLinkAsync(message, config)
            : null;

        if (detection is null)
        {
            if (string.IsNullOrWhiteSpace(message.Content) && message.AttachmentCount == 0)
                return Unit.Value;

            detection = await RecordAndDetectRepostAsync(message, config);
            if (detection is null)
                return Unit.Value;
        }

        var cooldown = TimeSpan.FromSeconds(config.DetectionWindowSeconds);
        if (!await _messageRepository.TryClaimActionAsync(message.GuildId, message.AuthorId, cooldown))
        {
            if (config.DeleteMessages)
                await _discord.BulkDeleteMessagesAsync(message.GuildId, [(message.ChannelId, message.MessageId)]);
            return Unit.Value;
        }

        var incident = SpamIncident.Raise(
            message.GuildId, message.AuthorId, message.AuthorUsername,
            detection.IncidentContent, detection.ChannelIds);
        await PersistIncidentAsync(incident, ct);

        var muteApplied = config.MuteOnSpam
            ? (bool?)await _discord.MuteUserAsync(message.GuildId, message.AuthorId, TimeSpan.FromMinutes(config.MuteDurationMinutes))
            : null;

        if (config.AlertChannelId is { } alertChannelId)
        {
            if (detection.NewUserMemberFor is { } memberFor)
                await _discord.SendNewUserLinkAlertAsync(
                    message.GuildId, alertChannelId, incident, memberFor, config, message.AttachmentUrls, muteApplied);
            else
                await _discord.SendAlertAsync(
                    message.GuildId, alertChannelId, incident, config, message.AttachmentUrls, muteApplied);
        }

        if (config.DeleteMessages)
            await _discord.BulkDeleteMessagesAsync(message.GuildId, detection.MessagesToDelete);

        return Unit.Value;
    }

    private async Task<GuildConfig> LoadConfigAsync(ulong guildId, CancellationToken ct)
    {
        var snapshot = await _cache.GetOrSetAsync(
            CacheKeys.GuildConfig(guildId),
            async token =>
            {
                await using var context = await _db.CreateDbContextAsync(token);
                var config = await context.GuildConfigs.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.GuildId == guildId, token);
                if (config != null)
                    return config.ToSnapshot();

                config = GuildConfig.CreateDefault(guildId);
                context.GuildConfigs.Add(config);
                await context.SaveChangesAsync(token);

                return config.ToSnapshot();
            },
            ConfigCacheTtl,
            ct);

        return GuildConfig.FromSnapshot(snapshot);
    }

    private async Task<Detection?> DetectNewUserLinkAsync(MessageReceivedEvent message, GuildConfig config)
    {
        var verdict = LinkPolicy.Evaluate(message.Content, message.GuildId, config);
        if (verdict is LinkVerdict.NoLinks or LinkVerdict.Allowed)
            return null;

        var joinedAt = message.AuthorJoinedAt ?? await _discord.GetUserJoinedAtAsync(message.GuildId, message.AuthorId);
        if (joinedAt is null)
            return null;

        var memberFor = DateTimeOffset.UtcNow - joinedAt.Value;
        if (memberFor >= TimeSpan.FromHours(config.NewUserHoursThreshold))
            return null;

        if (verdict == LinkVerdict.PendingInviteVerification && await AllInvitesAllowedAsync(message.Content, config))
            return null;

        _logger.LogWarning(
            "SUSPICIOUS: New user {User} ({Id}) posted link, member for {MemberFor} (threshold: {Threshold}h) in guild {Guild}",
            message.AuthorUsername, message.AuthorId, memberFor, config.NewUserHoursThreshold, message.GuildId);

        return new Detection(
            $"[NEW USER - joined {FormatDuration(memberFor)} ago] {message.Content}",
            [message.ChannelId],
            [(message.ChannelId, message.MessageId)],
            memberFor);
    }

    private async Task<Detection?> RecordAndDetectRepostAsync(MessageReceivedEvent message, GuildConfig config)
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
            return null;

        _logger.LogWarning(
            "SPAM DETECTED: User {User} ({Id}) - {Channels} channels, reason: {Reason}, similarity: {Similarity:P0}",
            message.AuthorUsername, message.AuthorId, verdict.ChannelCount, verdict.Reason, verdict.MaxSimilarity);

        return new Detection(
            message.Content,
            verdict.ChannelIds,
            verdict.MatchingMessages.Select(m => (m.ChannelId, m.MessageId)).ToList(),
            null);
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

    private async Task PersistIncidentAsync(SpamIncident incident, CancellationToken ct)
    {
        await using var context = await _db.CreateDbContextAsync(ct);
        context.SpamIncidents.Add(incident);
        await context.SaveChangesAsync(ct);
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
