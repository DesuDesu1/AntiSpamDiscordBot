using System.Net.Http;
using System.Net.Http.Json;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Data.Entities;
using Discord;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Services.Discord;

public class DiscordService
{
    private readonly DiscordRestClient _client;
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly ILogger<DiscordService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public DiscordService(
        DiscordRestClient client,
        IDbContextFactory<BotDbContext> dbFactory,
        ILogger<DiscordService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _client = client;
        _dbFactory = dbFactory;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    private HttpClient Http => _httpClientFactory.CreateClient(nameof(DiscordService));

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];
    private const long MaxRehostBytes = 8 * 1024 * 1024;        // per-image cap (Discord basic upload limit)
    private const long MaxRehostTotalBytes = 20 * 1024 * 1024;  // budget across all images in one alert
    private const int MaxRehostImages = 4;                       // Discord shows at most 4 images in a merged gallery

    // Embeds that share the same url get merged by Discord into a single image gallery,
    // which is how one alert can show several pictures.
    private const string GalleryUrl = "https://discord.com";

    /// <summary>
    /// Downloads up to <see cref="MaxRehostImages"/> image attachments so they can be re-uploaded
    /// with the alert. Re-hosting (instead of linking the original CDN url) keeps the pictures visible
    /// after the spam message is deleted and after the embed is edited on action. Non-image attachments
    /// (videos, files) are ignored, and the per-image and total size budgets are enforced.
    /// </summary>
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
                // Prefix with the index so two attachments with the same name stay distinct references.
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

    /// <summary>
    /// Sends the alert, re-uploading any images and merging them into a gallery via the shared url trick.
    /// </summary>
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

    /// <summary>
    /// Times the user out. Returns false on failure - most often because the target outranks
    /// the bot (owner or a higher role), which Discord rejects.
    /// </summary>
    public async Task<bool> MuteUserAsync(ulong guildId, ulong userId, TimeSpan duration)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            if (guild == null)
            {
                _logger.LogWarning("Guild {GuildId} not found", guildId);
                return false;
            }

            var user = await guild.GetUserAsync(userId);
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
            var guild = await _client.GetGuildAsync(guildId);
            var user = await guild.GetUserAsync(userId);
            
            if (user != null && user.TimedOutUntil.HasValue && user.TimedOutUntil.Value > DateTimeOffset.UtcNow)
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

    /// <summary>
    /// Bans the user. Returns false on failure - most often because the target outranks
    /// the bot (owner or a higher role), which Discord rejects.
    /// </summary>
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

    public async Task SendFollowupAsync(string token, string message)
    {
        try
        {
            var applicationId = _client.CurrentUser.Id;
            var url = $"https://discord.com/api/v10/webhooks/{applicationId}/{token}";
            
            var payload = new { content = message, flags = 64 };
            await Http.PostAsJsonAsync(url, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send followup via DiscordService");
        }
    }

    public async Task BulkDeleteMessagesAsync(ulong guildId, List<(ulong ChannelId, ulong MessageId)> messages)
    {
        var guild = await _client.GetGuildAsync(guildId);
        if (guild == null) return;
        
        var deletedCount = 0;
        var byChannel = messages.GroupBy(m => m.ChannelId);
        
        foreach (var group in byChannel)
        {
            try
            {
                var channels = await guild.GetChannelsAsync();
                var channel = channels.FirstOrDefault(c => c.Id == group.Key) as ITextChannel;
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

    /// <summary>
    /// Builds the mute status line for an alert: applied, failed (likely outranks the bot), or disabled.
    /// </summary>
    private static string MuteStatusLine(bool? muteApplied, GuildConfig config) => muteApplied switch
    {
        true => $"Muted for {config.MuteDurationMinutes} minutes",
        false => "⚠️ Could not mute - the user may outrank the bot",
        null => "Not muted (mute disabled)"
    };

    public async Task SendAlertAsync(ulong guildId, ulong channelId, SpamIncident incident, GuildConfig config, IReadOnlyList<string> attachmentUrls, bool? muteApplied)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            if (guild == null)
            {
                _logger.LogWarning("Guild {GuildId} not found", guildId);
                return;
            }
            
            var channels = await guild.GetChannelsAsync();
            var channel = channels.FirstOrDefault(c => c.Id == channelId) as ITextChannel;
            if (channel == null)
            {
                _logger.LogWarning("Alert channel {ChannelId} not found", channelId);
                return;
            }

            var contentDisplay = string.IsNullOrWhiteSpace(incident.Content)
                ? "*[No text - attachment spam]*"
                : (incident.Content.Length > 200 ? incident.Content[..200] + "..." : incident.Content);

            var images = await DownloadImagesAsync(attachmentUrls);

            var embedBuilder = new EmbedBuilder()
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

            await using var db = await _dbFactory.CreateDbContextAsync();
            var dbIncident = await db.SpamIncidents.FindAsync(incident.Id);
            if (dbIncident != null)
            {
                dbIncident.AlertMessageId = alertMessage.Id;
                dbIncident.AlertChannelId = channelId;
                await db.SaveChangesAsync();
            }
            
            _logger.LogInformation("Sent alert for incident #{Id}", incident.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert for incident #{Id}", incident.Id);
        }
    }

    public async Task UpdateAlertMessageAsync(ulong guildId, SpamIncident incident, string action, string moderator, bool actionFailed = false)
    {
        if (incident.AlertMessageId == null || incident.AlertChannelId == null)
            return;

        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            if (guild == null) return;
            
            var channels = await guild.GetChannelsAsync();
            var channel = channels.FirstOrDefault(c => c.Id == incident.AlertChannelId.Value) as ITextChannel;
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

            // Keep the re-hosted images in the embed gallery: the alert message still carries the
            // attachments, so editing preserves them as long as we don't touch m.Attachments.
            // Image attachments report a Width; rebuild the same shared-url gallery over them.
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

    public async Task SendNewUserLinkAlertAsync(ulong guildId, ulong channelId, SpamIncident incident, TimeSpan? memberFor, GuildConfig config, IReadOnlyList<string> attachmentUrls, bool? muteApplied)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            if (guild == null)
            {
                _logger.LogWarning("Guild {GuildId} not found", guildId);
                return;
            }
            
            var channels = await guild.GetChannelsAsync();
            var channel = channels.FirstOrDefault(c => c.Id == channelId) as ITextChannel;
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

            var embedBuilder = new EmbedBuilder()
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

            await using var db = await _dbFactory.CreateDbContextAsync();
            var dbIncident = await db.SpamIncidents.FindAsync(incident.Id);
            if (dbIncident != null)
            {
                dbIncident.AlertMessageId = alertMessage.Id;
                dbIncident.AlertChannelId = channelId;
                await db.SaveChangesAsync();
            }
            
            _logger.LogInformation("Sent new user link alert for incident #{Id}", incident.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send new user link alert for incident #{Id}", incident.Id);
        }
    }

    /// <summary>
    /// Fetches user's JoinedAt from Discord API (fallback when Gateway cache misses).
    /// Only called when a user posts a link and JoinedAt wasn't in the message event.
    /// </summary>
    public async Task<DateTimeOffset?> GetUserJoinedAtAsync(ulong guildId, ulong userId)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            if (guild == null)
                return null;

            var user = await guild.GetUserAsync(userId);
            
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

    /// <summary>
    /// Resolves a discord.gg invite code to get the guild ID it points to.
    /// Returns null if invite is invalid or couldn't be resolved.
    /// </summary>
    public async Task<ulong?> ResolveInviteGuildIdAsync(string inviteCode)
    {
        try
        {
            var invite = await _client.GetInviteAsync(inviteCode);
            var guildId = invite?.GuildId;
            
            _logger.LogDebug("Resolved invite {Code} to guild {GuildId}", inviteCode, guildId);
            return guildId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve invite {Code}", inviteCode);
            return null;
        }
    }
}
