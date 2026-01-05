using AntiSpam.Bot.Services;
using System.Text.Json;
using Discord.Rest;

namespace AntiSpam.Bot.Features.GuildManagement;

public class GuildCommandHandler
{
    private readonly GuildConfigService _configService;
    private readonly DiscordRestClient _discord;
    private readonly ILogger<GuildCommandHandler> _logger;

    public GuildCommandHandler(
        GuildConfigService configService, 
        DiscordRestClient discord,
        ILogger<GuildCommandHandler> logger)
    {
        _configService = configService;
        _discord = discord;
        _logger = logger;
    }

    public async Task<string> HandleCommandAsync(ulong guildId, string subCommand, Dictionary<string, object> options)
    {
        _logger.LogInformation("Handling command {Command} for guild {Guild} with options: {Options}", 
            subCommand, guildId, string.Join(", ", options.Select(kv => $"{kv.Key}={kv.Value} ({kv.Value?.GetType().Name})")));
        
        return subCommand switch
        {
            "status" => await HandleStatusAsync(guildId),
            "enable" => await HandleEnableAsync(guildId, GetBool(options, "enabled")),
            "alert-channel" => await HandleAlertChannelAsync(guildId, GetULong(options, "channel")),
            "min-channels" => await HandleMinChannelsAsync(guildId, GetInt(options, "count")),
            "similarity" => await HandleSimilarityAsync(guildId, GetInt(options, "percent")),
            "window" => await HandleWindowAsync(guildId, GetInt(options, "seconds")),
            "mute" => await HandleMuteAsync(guildId, GetBool(options, "enabled"), 
                options.ContainsKey("duration") ? GetInt(options, "duration") : 60),
            "delete" => await HandleDeleteAsync(guildId, GetBool(options, "enabled")),
            _ => "❌ Unknown command"
        };
    }

    // Helper methods to extract values from JsonElement or primitive types
    private static ulong GetULong(Dictionary<string, object> options, string key)
    {
        if (!options.TryGetValue(key, out var value)) return 0;
        
        return value switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetUInt64(),
            JsonElement je when je.ValueKind == JsonValueKind.String => ulong.Parse(je.GetString()!),
            ulong u => u,
            long l => (ulong)l,
            int i => (ulong)i,
            string s => ulong.Parse(s),
            _ => Convert.ToUInt64(value)
        };
    }

    private static int GetInt(Dictionary<string, object> options, string key)
    {
        if (!options.TryGetValue(key, out var value)) return 0;
        
        return value switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
            JsonElement je when je.ValueKind == JsonValueKind.String => int.Parse(je.GetString()!),
            int i => i,
            long l => (int)l,
            string s => int.Parse(s),
            _ => Convert.ToInt32(value)
        };
    }

    private static bool GetBool(Dictionary<string, object> options, string key)
    {
        if (!options.TryGetValue(key, out var value)) return false;
        
        return value switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.True => true,
            JsonElement je when je.ValueKind == JsonValueKind.False => false,
            JsonElement je when je.ValueKind == JsonValueKind.String => bool.Parse(je.GetString()!),
            bool b => b,
            string s => bool.Parse(s),
            _ => Convert.ToBoolean(value)
        };
    }

    private async Task<string> HandleStatusAsync(ulong guildId)
    {
        var config = await _configService.GetOrCreateAsync(guildId);
        
        // Get channel name if set
        string alertChannelDisplay = "Not set";
        if (config.AlertChannelId.HasValue)
        {
            try
            {
                var guild = await _discord.GetGuildAsync(guildId);
                var channel = await guild.GetTextChannelAsync(config.AlertChannelId.Value);
                alertChannelDisplay = $"#{channel?.Name ?? config.AlertChannelId.Value.ToString()}";
            }
            catch
            {
                alertChannelDisplay = $"ID: {config.AlertChannelId.Value}";
            }
        }
        
        return $"""
            📊 **Anti-Spam Settings**
            
            Protection: {(config.IsEnabled ? "✅ Enabled" : "❌ Disabled")}
            Alert Channel: {alertChannelDisplay}
            
            **Detection:**
            • Min channels: {config.MinChannelsForSpam}
            • Text similarity: {config.SimilarityThreshold:P0}
            • Time window: {config.DetectionWindowSeconds} sec
            
            **Actions:**
            • Mute: {(config.MuteOnSpam ? $"✅ {config.MuteDurationMinutes} min" : "❌")}
            • Delete messages: {(config.DeleteMessages ? "✅" : "❌")}
            """;
    }

    private async Task<string> HandleEnableAsync(ulong guildId, bool enabled)
    {
        await _configService.SetEnabledAsync(guildId, enabled);
        return enabled ? "✅ Protection enabled" : "❌ Protection disabled";
    }

    private async Task<string> HandleAlertChannelAsync(ulong guildId, ulong channelId)
    {
        _logger.LogInformation("Setting alert channel {ChannelId} for guild {GuildId}", channelId, guildId);
        await _configService.SetAlertChannelAsync(guildId, channelId);
        
        // Get channel name for display
        try
        {
            var guild = await _discord.GetGuildAsync(guildId);
            var channel = await guild.GetTextChannelAsync(channelId);
            return $"✅ Alert channel set to #{channel?.Name ?? channelId.ToString()}";
        }
        catch
        {
            return $"✅ Alert channel set (ID: {channelId})";
        }
    }

    private async Task<string> HandleMinChannelsAsync(ulong guildId, int count)
    {
        var success = await _configService.SetMinChannelsAsync(guildId, count);
        return success 
            ? $"✅ Minimum channels: {count}" 
            : "❌ Count must be 2-10";
    }

    private async Task<string> HandleSimilarityAsync(ulong guildId, int percent)
    {
        var success = await _configService.SetSimilarityThresholdAsync(guildId, percent);
        return success 
            ? $"✅ Similarity threshold: {percent}%" 
            : "❌ Percent must be 50-100";
    }

    private async Task<string> HandleWindowAsync(ulong guildId, int seconds)
    {
        var success = await _configService.SetDetectionWindowAsync(guildId, seconds);
        return success 
            ? $"✅ Detection window: {seconds}s" 
            : "❌ Window must be 10-600 seconds";
    }

    private async Task<string> HandleMuteAsync(ulong guildId, bool enabled, int durationMinutes)
    {
        await _configService.SetMuteSettingsAsync(guildId, enabled, durationMinutes);
        return enabled 
            ? $"✅ Mute enabled ({durationMinutes} min)" 
            : "❌ Mute disabled";
    }

    private async Task<string> HandleDeleteAsync(ulong guildId, bool enabled)
    {
        await _configService.SetDeleteMessagesAsync(guildId, enabled);
        return enabled 
            ? "✅ Auto-delete spam enabled" 
            : "❌ Auto-delete spam disabled";
    }
}
