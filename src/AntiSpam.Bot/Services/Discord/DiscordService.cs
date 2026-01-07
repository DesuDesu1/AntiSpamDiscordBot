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

    public DiscordService(
        DiscordRestClient client,
        IDbContextFactory<BotDbContext> dbFactory,
        ILogger<DiscordService> logger)
    {
        _client = client;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task MuteUserAsync(ulong guildId, ulong userId, TimeSpan duration)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            if (guild == null)
            {
                _logger.LogWarning("Guild {GuildId} not found", guildId);
                return;
            }
            
            var user = await guild.GetUserAsync(userId);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found in guild {GuildId}", userId, guildId);
                return;
            }

            await user.ModifyAsync(x => x.TimedOutUntil = DateTimeOffset.UtcNow.Add(duration));
            _logger.LogInformation("Muted user {UserId} for {Duration}", userId, duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mute user {UserId}", userId);
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

    public async Task BanUserAsync(ulong guildId, ulong userId, string reason)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            await guild.AddBanAsync(userId, 1, reason);
            _logger.LogInformation("Banned user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ban user {UserId}", userId);
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

    public async Task SendAlertAsync(ulong guildId, ulong channelId, SpamIncident incident, GuildConfig config)
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

            var embed = new EmbedBuilder()
                .WithTitle("🚨 Spam Detected")
                .WithColor(Color.Red)
                .WithDescription($"**User:** <@{incident.UserId}> ({incident.Username})\n" +
                                 $"**Channels:** {incident.ChannelIds.Count}\n" +
                                 $"**Status:** Muted for {config.MuteDurationMinutes} minutes")
                .AddField("Content", contentDisplay)
                .AddField("Channels", string.Join(", ", incident.ChannelIds.Select(id => $"<#{id}>")))
                .AddField("Actions", "🔨 Ban • ✅ Release")
                .WithFooter($"Incident #{incident.Id}")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            var components = new ComponentBuilder()
                .WithButton("Ban", $"spam_ban_{incident.Id}", ButtonStyle.Danger, new Emoji("🔨"))
                .WithButton("Release", $"spam_release_{incident.Id}", ButtonStyle.Success, new Emoji("✅"))
                .Build();

            var alertMessage = await channel.SendMessageAsync(embed: embed, components: components);
            
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

    public async Task UpdateAlertMessageAsync(ulong guildId, SpamIncident incident, string action, string moderator)
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

            var color = action == "Banned" ? Color.DarkRed : Color.Green;
            var emoji = action == "Banned" ? "🔨" : "✅";

            var contentDisplay = string.IsNullOrWhiteSpace(incident.Content)
                ? "*[No text - attachment spam]*"
                : (incident.Content.Length > 200 ? incident.Content[..200] + "..." : incident.Content);

            var embed = new EmbedBuilder()
                .WithTitle($"{emoji} Spam Incident - {action}")
                .WithColor(color)
                .WithDescription($"**User:** <@{incident.UserId}> ({incident.Username})\n" +
                                 $"**Status:** {action} by {moderator}")
                .AddField("Content", contentDisplay)
                .WithFooter($"Incident #{incident.Id}")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await message.ModifyAsync(m =>
            {
                m.Embed = embed;
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update alert for incident #{Id}", incident.Id);
        }
    }

    public async Task SendNewUserLinkAlertAsync(ulong guildId, ulong channelId, SpamIncident incident, TimeSpan? memberFor, GuildConfig config)
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

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ Suspicious New User - Link Posted")
                .WithColor(Color.Orange)
                .WithDescription($"**User:** <@{incident.UserId}> ({incident.Username})\n" +
                                 $"**Member for:** {memberForDisplay} (threshold: {config.NewUserHoursThreshold}h)\n" +
                                 $"**Channel:** <#{incident.ChannelIds.FirstOrDefault()}>\n" +
                                 $"**Status:** Muted for {config.MuteDurationMinutes} minutes")
                .AddField("Content", contentDisplay)
                .AddField("Why flagged?", $"User joined {memberForDisplay} ago and posted a link")
                .AddField("Actions", "🔨 Ban • ✅ Release")
                .WithFooter($"Incident #{incident.Id}")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            var components = new ComponentBuilder()
                .WithButton("Ban", $"spam_ban_{incident.Id}", ButtonStyle.Danger, new Emoji("🔨"))
                .WithButton("Release", $"spam_release_{incident.Id}", ButtonStyle.Success, new Emoji("✅"))
                .Build();

            var alertMessage = await channel.SendMessageAsync(embed: embed, components: components);
            
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
}
