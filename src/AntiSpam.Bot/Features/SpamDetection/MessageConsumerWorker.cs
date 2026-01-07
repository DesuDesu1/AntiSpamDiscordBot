using System.Text.Json;
using System.Text.RegularExpressions;
using AntiSpam.Bot.Services.Cache;
using AntiSpam.Bot.Services.Discord;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;

namespace AntiSpam.Bot.Features.SpamDetection;

public partial class MessageConsumerWorker : BackgroundService
{
    private readonly IConsumer<string, string> _consumer;
    private readonly SpamDetector _spamDetector;
    private readonly SpamActionService _spamAction;
    private readonly DiscordService _discord;
    private readonly ILogger<MessageConsumerWorker> _logger;

    public MessageConsumerWorker(
        IConsumer<string, string> consumer,
        SpamDetector spamDetector,
        SpamActionService spamAction,
        DiscordService discord,
        ILogger<MessageConsumerWorker> logger)
    {
        _consumer = consumer;
        _spamDetector = spamDetector;
        _spamAction = spamAction;
        _discord = discord;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(KafkaTopics.Messages);
        _logger.LogInformation("Subscribed to topic: {Topic}", KafkaTopics.Messages);

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = _consumer.Consume(TimeSpan.FromSeconds(1));
                if (result == null) continue;

                var message = JsonSerializer.Deserialize<MessageReceivedEvent>(result.Message.Value);
                if (message == null) continue;

                if (message.IsBot)
                {
                    _logger.LogDebug("Ignoring bot message from {Author}", message.AuthorUsername);
                    continue;
                }

                _logger.LogDebug("Processing message from {Author} in guild {Guild}", 
                    message.AuthorUsername, message.GuildId);

                await ProcessMessageAsync(message);

                _consumer.Commit(result);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message");
            }
        }
    }

    private async Task ProcessMessageAsync(MessageReceivedEvent message)
    {
        var config = await _spamAction.GetOrCreateGuildConfigAsync(message.GuildId);
        
        if (!config.IsEnabled)
            return;

        // Check for suspicious new user posting links
        if (config.DetectNewUserLinks && ContainsSuspiciousLink(message.Content, message.GuildId, config.AllowedLinks, out var hasExternalLink))
        {
            if (hasExternalLink)
            {
                var threshold = TimeSpan.FromHours(config.NewUserHoursThreshold);
            
                // Try from message first (Gateway cache), fallback to API if missing
                var joinedAt = message.AuthorJoinedAt 
                    ?? await _discord.GetUserJoinedAtAsync(message.GuildId, message.AuthorId);
            
                var (isNew, memberFor) = IsNewUser(joinedAt, threshold);
            
                if (isNew)
                {
                    // Double-check discord.gg invites via API (they might be same-server)
                    if (await ContainsOnlyOwnServerInvitesAsync(message.Content, message.GuildId))
                    {
                        _logger.LogDebug("New user {User} posted invite to own server, allowing", message.AuthorUsername);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "SUSPICIOUS: New user {User} ({Id}) posted link, member for {MemberFor} (threshold: {Threshold}h) in guild {Guild}",
                            message.AuthorUsername, message.AuthorId, memberFor, config.NewUserHoursThreshold, message.GuildId);

                        await _spamAction.HandleSuspiciousNewUserAsync(
                            message.GuildId,
                            message.AuthorId,
                            message.AuthorUsername,
                            message.Content,
                            message.ChannelId,
                            message.MessageId,
                            memberFor);
                        return;
                    }
                }
            }
        }
        // Skip if no text and no attachments
        if (string.IsNullOrWhiteSpace(message.Content) && message.AttachmentCount == 0)
            return;

        var cachedMsg = new CachedMessage(
            message.Content,
            message.ChannelId, 
            message.MessageId, 
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            message.AttachmentCount);

        var options = new SpamDetectionOptions
        {
            MinChannels = config.MinChannelsForSpam,
            SimilarityThreshold = config.SimilarityThreshold,
            Window = TimeSpan.FromSeconds(config.DetectionWindowSeconds)
        };

        var result = await _spamDetector.CheckAndAddAsync(
            message.GuildId,
            message.AuthorId,
            cachedMsg,
            options);

        if (result.IsSpam)
        {
            _logger.LogWarning(
                "SPAM DETECTED: User {User} ({Id}) - {Channels} channels, reason: {Reason}, similarity: {Similarity:P0}",
                message.AuthorUsername, message.AuthorId, result.ChannelCount, result.Reason, result.MaxSimilarity);

            var messagesToDelete = result.MatchingMessages
                .Select(m => (m.ChannelId, m.MessageId))
                .ToList();

            await _spamAction.HandleSpamDetectedAsync(
                message.GuildId,
                message.AuthorId,
                message.AuthorUsername,
                message.Content,
                result.ChannelIds,
                messagesToDelete);
        }
    }

    private static (bool IsNew, TimeSpan? MemberFor) IsNewUser(DateTimeOffset? joinedAt, TimeSpan threshold)
    {
        if (joinedAt == null)
            return (false, null); // Can't determine, assume not new
            
        var memberFor = DateTimeOffset.UtcNow - joinedAt.Value;
        return (memberFor < threshold, memberFor);
    }

    /// <summary>
    /// Checks if content contains links that could be suspicious.
    /// Returns false if all links point to the same server (discord.com/channels/GUILD_ID/...)
    /// </summary>
    private static bool ContainsSuspiciousLink(string? content, ulong currentGuildId, List<string> allowedLinks, out bool hasExternalLink)
    {
        hasExternalLink = false;
        
        if (string.IsNullOrWhiteSpace(content))
            return false;

        // Extract all URLs from content
        var urlMatches = UrlRegex().Matches(content);
        
        foreach (Match match in urlMatches)
        {
            var url = match.Value.ToLowerInvariant();
            var domain = ExtractDomain(url);
            
            // Skip allowed links (can be domain or full path like youtube.com/channel/xyz)
            var urlPath = ExtractUrlPath(url);
            if (allowedLinks.Any(allowed => 
                urlPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
                domain.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                domain.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            
            // Check discord.com/channels links - verify guild ID
            var channelMatch = DiscordChannelLinkRegex().Match(url);
            if (channelMatch.Success)
            {
                if (ulong.TryParse(channelMatch.Groups[1].Value, out var guildId) && guildId == currentGuildId)
                    continue; // Same server, skip
                
                hasExternalLink = true;
                return true;
            }
            
            // Check discord.gg invites - will be verified via API
            if (DiscordInviteRegex().IsMatch(url))
            {
                hasExternalLink = true; // Will be verified via API later
                return true;
            }
            
            // Any other URL is suspicious
            if (!domain.Contains("discord.com") && !domain.Contains("discordapp.com"))
            {
                hasExternalLink = true;
                return true;
            }
        }

        return false;
    }

    private static string ExtractDomain(string url)
    {
        // Remove protocol
        if (url.StartsWith("https://"))
            url = url[8..];
        else if (url.StartsWith("http://"))
            url = url[7..];
        
        // Get domain part
        var slashIndex = url.IndexOf('/');
        if (slashIndex > 0)
            url = url[..slashIndex];
        
        return url;
    }

    private static string ExtractUrlPath(string url)
    {
        // Remove protocol and return domain + path
        if (url.StartsWith("https://"))
            url = url[8..];
        else if (url.StartsWith("http://"))
            url = url[7..];
        
        return url.TrimEnd('/');
    }

    /// <summary>
    /// Resolves discord.gg invites to check if they're for the current server
    /// </summary>
    private async Task<bool> ContainsOnlyOwnServerInvitesAsync(string content, ulong currentGuildId)
    {
        var inviteMatches = DiscordInviteRegex().Matches(content);
        if (inviteMatches.Count == 0)
            return true; // No invites to check
        
        foreach (Match match in inviteMatches)
        {
            var code = match.Groups[1].Value;
            var inviteGuildId = await _discord.ResolveInviteGuildIdAsync(code);
            
            if (inviteGuildId == null || inviteGuildId != currentGuildId)
                return false; // External or unknown invite
        }
        
        return true; // All invites are for this server
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?:discord\.gg|discord\.com/invite)/([a-zA-Z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordInviteRegex();

    [GeneratedRegex(@"discord\.com/channels/(\d+)/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordChannelLinkRegex();

    public override void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
        base.Dispose();
    }
}
