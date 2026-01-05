using AntiSpam.Bot.Data;
using AntiSpam.Bot.Data.Entities;
using Discord;
using Discord.Rest;

namespace AntiSpam.Bot.Services.Discord;

public class DiscordService
{
    private readonly DiscordRestClient _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordService> _logger;

    public DiscordService(
        DiscordRestClient client,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordService> logger)
    {
        _client = client;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task MuteUserAsync(ulong guildId, ulong userId, TimeSpan duration)
    {
        try
        {
            var guild = await _client.GetGuildAsync(guildId);
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
        var deletedCount = 0;
        
        var byChannel = messages.GroupBy(m => m.ChannelId);
        
        foreach (var group in byChannel)
        {
            try
            {
                var channel = await guild.GetTextChannelAsync(group.Key);
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

    public async Task SendAlertAsync(ulong channelId, SpamIncident incident, GuildConfig config)
    {
        try
        {
            var channel = await _client.GetChannelAsync(channelId) as ITextChannel;
            if (channel == null)
            {
                _logger.LogWarning("Alert channel {ChannelId} not found", channelId);
                return;
            }

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
                .WithFooter($"Incident #{incident.Id}")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            var components = new ComponentBuilder()
                .WithButton("Ban", $"spam_ban_{incident.Id}", ButtonStyle.Danger)
                .WithButton("Release", $"spam_release_{incident.Id}", ButtonStyle.Success)
                .Build();

            await channel.SendMessageAsync(embed: embed, components: components);
            
            _logger.LogInformation("Sent alert for incident #{Id} to channel {ChannelId}", 
                incident.Id, channelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert for incident #{Id}", incident.Id);
        }
    }
}
