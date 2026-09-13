using System.Net.Http;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Domain.GuildManagement;
using Discord;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;
using AntiSpam.Bot.Infrastructure.Cache;
using ZiggyCreatures.Caching.Fusion;
using SpamIncidentEntity = AntiSpam.Bot.Domain.SpamIncident.SpamIncident;

namespace AntiSpam.Bot.Infrastructure.Discord;

public class DiscordService
{
    private readonly DiscordRestClient _client;
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly ILogger<DiscordService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFusionCache _cache;

    private static readonly FusionCacheEntryOptions ChannelCacheOptions =
        new(TimeSpan.FromMinutes(5))
        {
            SkipDistributedCacheRead = true,
            SkipDistributedCacheWrite = true,
            SkipBackplaneNotifications = true
        };

    public DiscordService(
        DiscordRestClient client,
        IDbContextFactory<BotDbContext> dbFactory,
        ILogger<DiscordService> logger,
        IHttpClientFactory httpClientFactory,
        IFusionCache cache)
    {
        _client = client;
        _dbFactory = dbFactory;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    private async Task<ITextChannel?> GetTextChannelAsync(ulong channelId) =>
        await _cache.GetOrSetAsync<ITextChannel?>(
            CacheKeys.Channel(channelId),
            async _ => await _client.GetChannelAsync(channelId) as ITextChannel,
            ChannelCacheOptions);

    private HttpClient Http => _httpClientFactory.CreateClient(nameof(DiscordService));

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];
    private const long MaxRehostBytes = 8 * 1024 * 1024;
    private const long MaxRehostTotalBytes = 20 * 1024 * 1024;
    private const int MaxRehostImages = 4;
    
    private const string GalleryUrl = "https://discord.com";
    
    private async Task<List<(string FileName, byte[] Bytes)>> DownloadImagesAsync(IReadOnlyList<string> attachmentUrls)
    {
        var images = new List<(string FileName, byte[] Bytes)>();
        long total = 0;

        foreach (var url in attachmentUrls)
        {
            if (images.Count >= MaxRehostImages)
                break;
            if (!IsImageUrl(url))
                continue;

            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                if (bytes.LongLength > MaxRehostBytes)
                {
                    _logger.LogInformation("Skipping attachment image too large to re-host ({Bytes} bytes)", bytes.LongLength);
                    continue;
                }
                if (total + bytes.LongLength > MaxRehostTotalBytes)
                    break;

                total += bytes.LongLength;
                var fileName = $"{images.Count}_{SanitizeFileName(Path.GetFileName(UrlPath(url)))}";
                images.Add((fileName, bytes));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download attachment image for re-hosting");
            }
        }

        return images;
    }
    
    private static async Task<IUserMessage> SendAlertWithImagesAsync(
        ITextChannel channel,
        EmbedBuilder mainEmbed,
        MessageComponent components,
        List<(string FileName, byte[] Bytes)> images)
    {
        if (images.Count == 0)
            return await channel.SendMessageAsync(embed: mainEmbed.Build(), components: components);

        mainEmbed.WithUrl(GalleryUrl).WithImageUrl($"attachment://{images[0].FileName}");
        var embeds = new List<Embed> { mainEmbed.Build() };
        foreach (var img in images.Skip(1))
            embeds.Add(new EmbedBuilder().WithUrl(GalleryUrl).WithImageUrl($"attachment://{img.FileName}").Build());

        var files = images.Select(i => new FileAttachment(new MemoryStream(i.Bytes), i.FileName));
        return await channel.SendFilesAsync(files, embeds: embeds.ToArray(), components: components);
    }

    private static bool IsImageUrl(string url)
        => ImageExtensions.Any(ext => UrlPath(url).EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static string UrlPath(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "attachment.png";

        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "attachment.png" : cleaned;
    }
    
    public async Task<bool> MuteUserAsync(ulong guildId, ulong userId, TimeSpan duration)
    {
        try
        {
            var user = await _client.GetGuildUserAsync(guildId, userId);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found in guild {GuildId}", userId, guildId);
                return false;
            }

            await user.ModifyAsync(x => x.TimedOutUntil = DateTimeOffset.UtcNow.Add(duration));
            _logger.LogInformation("Muted user {UserId} for {Duration}", userId, duration);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mute user {UserId}", userId);
            return false;
        }
    }

    public async Task UnmuteUserAsync(ulong guildId, ulong userId)
    {
        try
        {
            var user = await _client.GetGuildUserAsync(guildId, userId);

            if (user is { TimedOutUntil: not null } && user.TimedOutUntil.Value > DateTimeOffset.UtcNow)
            {
                await user.ModifyAsync(x => x.TimedOutUntil = DateTimeOffset.UtcNow);
                _logger.LogInformation("Unmuted user {UserId}", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unmute user {UserId}", userId);
        }
    }

    public async Task<bool> BanUserAsync(ulong guildId, ulong userId, string reason)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            await guild.AddBanAsync(userId, 1, reason);
            _logger.LogInformation("Banned user {UserId}", userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ban user {UserId}", userId);
            return false;
        }
    }

    public async Task BulkDeleteMessagesAsync(ulong guildId, List<(ulong ChannelId, ulong MessageId)> messages)
    {
        var deletedCount = 0;
        var byChannel = messages.GroupBy(m => m.ChannelId);

        foreach (var group in byChannel)
        {
            try
            {
                var channel = await GetTextChannelAsync(group.Key);
                if (channel == null) continue;

                var messageIds = group.Select(m => m.MessageId).ToArray();

                if (messageIds.Length == 1)
                {
                    await channel.DeleteMessageAsync(messageIds[0]);
                    deletedCount++;
                }
                else
                {
                    await channel.DeleteMessagesAsync(messageIds);
                    deletedCount += messageIds.Length;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete messages in channel {ChannelId}", group.Key);
            }
        }

        if (deletedCount > 0)
            _logger.LogInformation("Deleted {Count} spam messages", deletedCount);
    }

    private async Task<string?> TryGetAvatarUrlAsync(ulong guildId, ulong userId)
    {
        try
        {
            var member = await _client.GetGuildUserAsync(guildId, userId);
            if (member != null)
                return member.GetGuildAvatarUrl() ?? member.GetAvatarUrl() ?? member.GetDefaultAvatarUrl();

            var user = await _client.GetUserAsync(userId);
            return user?.GetAvatarUrl() ?? user?.GetDefaultAvatarUrl();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve avatar for user {UserId}", userId);
            return null;
        }
    }
    
    private static string MuteStatusLine(bool? muteApplied, GuildConfig config) => muteApplied switch
    {
        true => $"Muted for {config.MuteDurationMinutes} minutes",
        false => "⚠️ Could not mute - the user may outrank the bot",
        null => "Not muted (mute disabled)"
    };

    public async Task SendAlertAsync(ulong guildId, ulong channelId, SpamIncidentEntity incident, GuildConfig config, IReadOnlyList<string> attachmentUrls, bool? muteApplied)
    {
        try
        {
            var channel = await GetTextChannelAsync(channelId);
            if (channel == null)
            {
                _logger.LogWarning("Alert channel {ChannelId} not found", channelId);
                return;
            }

            var contentDisplay = string.IsNullOrWhiteSpace(incident.Content)
                ? "*[No text - attachment spam]*"
                : (incident.Content.Length > 200 ? incident.Content[..200] + "..." : incident.Content);

            var images = await DownloadImagesAsync(attachmentUrls);
            var avatarUrl = await TryGetAvatarUrlAsync(guildId, incident.UserId);

            var embedBuilder = new EmbedBuilder()
                .WithAuthor($"{incident.Username} ({incident.UserId})", avatarUrl)
                .WithTitle("🚨 Spam Detected")
                .WithColor(Color.Red)
                .WithDescription($"**User:** <@{incident.UserId}> ({incident.Username})\n" +
                                 $"**Channels:** {incident.ChannelIds.Count}\n" +
                                 $"**Status:** {MuteStatusLine(muteApplied, config)}")
                .AddField("Content", contentDisplay)
                .AddField("Channels", string.Join(", ", incident.ChannelIds.Select(id => $"<#{id}>")))
                .AddField("Actions", "🔨 Ban • ✅ Release")
                .WithFooter($"Incident #{incident.Id}")
                .WithTimestamp(DateTimeOffset.UtcNow);

            var components = new ComponentBuilder()
                .WithButton("Ban", $"spam_ban_{incident.Id}", ButtonStyle.Danger, new Emoji("🔨"))
                .WithButton("Release", $"spam_release_{incident.Id}", ButtonStyle.Success, new Emoji("✅"))
                .Build();

            var alertMessage = await SendAlertWithImagesAsync(channel, embedBuilder, components, images);

            await SaveAlertReferenceAsync(incident.Id, channelId, alertMessage.Id);

            _logger.LogInformation("Sent alert for incident #{Id}", incident.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert for incident #{Id}", incident.Id);
        }
    }

    public async Task UpdateAlertMessageAsync(ulong guildId, SpamIncidentEntity incident, string action, string moderator, bool actionFailed = false)
    {
        if (incident.AlertMessageId == null || incident.AlertChannelId == null)
            return;

        try
        {
            var channel = await GetTextChannelAsync(incident.AlertChannelId.Value);
            if (channel == null) return;

            var message = await channel.GetMessageAsync(incident.AlertMessageId.Value) as IUserMessage;
            if (message == null) return;

            var color = actionFailed ? Color.LightOrange : (action == "Banned" ? Color.DarkRed : Color.Green);
            var emoji = actionFailed ? "⚠️" : (action == "Banned" ? "🔨" : "✅");

            var contentDisplay = string.IsNullOrWhiteSpace(incident.Content)
                ? "*[No text - attachment spam]*"
                : (incident.Content.Length > 200 ? incident.Content[..200] + "..." : incident.Content);

            var statusLine = actionFailed
                ? $"⚠️ {action} failed - the user may outrank the bot. Handled by {moderator}"
                : $"{action} by {moderator}";

            var embedBuilder = new EmbedBuilder()
                .WithTitle($"{emoji} Spam Incident - {action}")
                .WithColor(color)
                .WithDescription($"**User:** <@{incident.UserId}> ({incident.Username})\n" +
                                 $"**Status:** {statusLine}")
                .AddField("Content", contentDisplay)
                .WithFooter($"Incident #{incident.Id}")
                .WithTimestamp(DateTimeOffset.UtcNow);
            
            var existingAuthor = message.Embeds.FirstOrDefault()?.Author;
            if (existingAuthor != null)
                embedBuilder.WithAuthor(existingAuthor.Value.Name, existingAuthor.Value.IconUrl);
            
            var existingImages = message.Attachments.Where(a => a.Width.HasValue).ToList();
            var embeds = new List<Embed>();
            if (existingImages.Count > 0)
            {
                embedBuilder.WithUrl(GalleryUrl).WithImageUrl(existingImages[0].Url);
                embeds.Add(embedBuilder.Build());
                foreach (var att in existingImages.Skip(1))
                    embeds.Add(new EmbedBuilder().WithUrl(GalleryUrl).WithImageUrl(att.Url).Build());
            }
            else
            {
                embeds.Add(embedBuilder.Build());
            }

            await message.ModifyAsync(m =>
            {
                m.Embeds = embeds.ToArray();
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update alert for incident #{Id}", incident.Id);
        }
    }

    public async Task SendNewUserLinkAlertAsync(ulong guildId, ulong channelId, SpamIncidentEntity incident, TimeSpan? memberFor, GuildConfig config, IReadOnlyList<string> attachmentUrls, bool? muteApplied)
    {
        try
        {
            var channel = await GetTextChannelAsync(channelId);
            if (channel == null)
            {
                _logger.LogWarning("Alert channel {ChannelId} not found", channelId);
                return;
            }

            var contentDisplay = incident.Content.Length > 300
                ? incident.Content[..300] + "..."
                : incident.Content;

            var memberForDisplay = memberFor.HasValue
                ? FormatDuration(memberFor.Value)
                : "unknown";

            var images = await DownloadImagesAsync(attachmentUrls);
            var avatarUrl = await TryGetAvatarUrlAsync(guildId, incident.UserId);

            var embedBuilder = new EmbedBuilder()
                .WithAuthor($"{incident.Username} ({incident.UserId})", avatarUrl)
                .WithTitle("⚠️ Suspicious New User - Link Posted")
                .WithColor(Color.Orange)
                .WithDescription($"**User:** <@{incident.UserId}> ({incident.Username})\n" +
                                 $"**Member for:** {memberForDisplay} (threshold: {config.NewUserHoursThreshold}h)\n" +
                                 $"**Channel:** <#{incident.ChannelIds.FirstOrDefault()}>\n" +
                                 $"**Status:** {MuteStatusLine(muteApplied, config)}")
                .AddField("Content", contentDisplay)
                .AddField("Why flagged?", $"User joined {memberForDisplay} ago and posted a link")
                .AddField("Actions", "🔨 Ban • ✅ Release")
                .WithFooter($"Incident #{incident.Id}")
                .WithTimestamp(DateTimeOffset.UtcNow);

            var components = new ComponentBuilder()
                .WithButton("Ban", $"spam_ban_{incident.Id}", ButtonStyle.Danger, new Emoji("🔨"))
                .WithButton("Release", $"spam_release_{incident.Id}", ButtonStyle.Success, new Emoji("✅"))
                .Build();

            var alertMessage = await SendAlertWithImagesAsync(channel, embedBuilder, components, images);

            await SaveAlertReferenceAsync(incident.Id, channelId, alertMessage.Id);

            _logger.LogInformation("Sent new user link alert for incident #{Id}", incident.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send new user link alert for incident #{Id}", incident.Id);
        }
    }
    
    private async Task SaveAlertReferenceAsync(long incidentId, ulong channelId, ulong alertMessageId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dbIncident = await db.SpamIncidents.FindAsync(incidentId);
        if (dbIncident == null)
            return;

        dbIncident.AttachAlert(channelId, alertMessageId);
        await db.SaveChangesAsync();
    }
    
    public async Task<DateTimeOffset?> GetUserJoinedAtAsync(ulong guildId, ulong userId)
    {
        try
        {
            var user = await _client.GetGuildUserAsync(guildId, userId);

            if (user?.JoinedAt != null)
            {
                _logger.LogDebug("Fetched JoinedAt from API for user {UserId}: {JoinedAt}", userId, user.JoinedAt);
            }

            return user?.JoinedAt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch JoinedAt for user {UserId} in guild {GuildId}", userId, guildId);
            return null;
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
    
    public async Task<(ulong GuildId, string GuildName)?> ResolveInviteAsync(string inviteCode)
    {
        try
        {
            var invite = await _client.GetInviteAsync(inviteCode);
            if (invite?.GuildId is not { } guildId)
                return null;

            _logger.LogDebug("Resolved invite {Code} to guild {GuildId}", inviteCode, guildId);
            return (guildId, invite.GuildName ?? guildId.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve invite {Code}", inviteCode);
            return null;
        }
    }
}
