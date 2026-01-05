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
            
            _logger.LogInformation("Muted user {UserId} in guild {GuildId} for {Duration}", 
                userId, guildId, duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mute user {UserId} in guild {GuildId}", userId, guildId);
        }
    }

    public async Task UnmuteUserAsync(ulong guildId, ulong userId)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            var user = await guild.GetUserAsync(userId);
            
            if (user != null)
            {
                await user.ModifyAsync(x => x.TimedOutUntil = null);
                _logger.LogInformation("Unmuted user {UserId} in guild {GuildId}", userId, guildId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unmute user {UserId} in guild {GuildId}", userId, guildId);
        }
    }

    public async Task BanUserAsync(ulong guildId, ulong userId, string reason)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
            await guild.AddBanAsync(userId, 1, reason);
            
            _logger.LogInformation("Banned user {UserId} from guild {GuildId}", userId, guildId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ban user {UserId} from guild {GuildId}", userId, guildId);
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
                // Get all channels and find the one we need
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
                _logger.LogWarning(ex, "Failed to bulk delete messages in channel {ChannelId}", group.Key);
            }
        }
        
        _logger.LogInformation("Bulk deleted {Count} messages in guild {GuildId}", deletedCount, guildId);
    }

    public async Task SendAlertAsync(ulong guildId, ulong channelId, SpamIncident incident, GuildConfig config)
    {
        try
        {
            _logger.LogInformation("Attempting to send alert to channel {ChannelId} in guild {GuildId}", 
                channelId, guildId);
            
            var guild = await _client.GetGuildAsync(guildId);
            if (guild == null)
            {
                _logger.LogWarning("Guild {GuildId} not found", guildId);
                return;
            }
            
            // Get all channels from guild and find the target channel
            var channels = await guild.GetChannelsAsync();
            _logger.LogInformation("Found {Count} channels in guild", channels.Count);
            
            var channel = channels.FirstOrDefault(c => c.Id == channelId) as ITextChannel;
            if (channel == null)
            {
                _logger.LogWarning("Channel {ChannelId} not found among {Count} channels. Available: {Channels}", 
                    channelId, channels.Count, 
                    string.Join(", ", channels.Select(c => $"{c.Name}({c.Id})")));
                return;
            }
            
            _logger.LogInformation("Found channel: {ChannelName}", channel.Name);

            var embed = new EmbedBuilder()
                .WithTitle("🚨 Spam Detected")
                .WithColor(Color.Red)
                .WithDescription($"**User:** <@{incident.UserId}> ({incident.Username})\n" +
                                 $"**Channels:** {incident.ChannelIds.Count}\n" +
                                 $"**Status:** Muted for {config.MuteDurationMinutes} minutes")
                .AddField("Content", incident.Content.Length > 200 
                    ? incident.Content[..200] + "..." 
                    : incident.Content)
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
            
            _logger.LogInformation("Alert message sent with ID {MessageId}", alertMessage.Id);
            
            await using var db = await _dbFactory.CreateDbContextAsync();
            var dbIncident = await db.SpamIncidents.FindAsync(incident.Id);
            if (dbIncident != null)
            {
                dbIncident.AlertMessageId = alertMessage.Id;
                dbIncident.AlertChannelId = channelId;
                await db.SaveChangesAsync();
            }
            
            _logger.LogInformation("Sent alert for incident #{Id} to channel {ChannelId}", incident.Id, channelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert for incident #{Id} to channel {ChannelId}", 
                incident.Id, channelId);
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

            var embed = new EmbedBuilder()
                .WithTitle($"{emoji} Spam Incident - {action}")
                .WithColor(color)
                .WithDescription($"**User:** <@{incident.UserId}> ({incident.Username})\n" +
                                 $"**Status:** {action} by {moderator}")
                .AddField("Content", incident.Content.Length > 200 
                    ? incident.Content[..200] + "..." 
                    : incident.Content)
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
            _logger.LogError(ex, "Failed to update alert message for incident #{Id}", incident.Id);
        }
    }
}
