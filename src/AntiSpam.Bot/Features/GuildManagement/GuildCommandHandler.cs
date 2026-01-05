namespace AntiSpam.Bot.Features.GuildManagement;

/// <summary>
/// Slash command handler for anti-spam configuration
/// </summary>
public class GuildCommandHandler
{
    private readonly GuildConfigService _configService;
    private readonly ILogger<GuildCommandHandler> _logger;

    public GuildCommandHandler(
        GuildConfigService configService,
        ILogger<GuildCommandHandler> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public async Task<string> HandleCommandAsync(ulong guildId, string subCommand, Dictionary<string, object> options)
    {
        _logger.LogInformation("Handling command {Command} for guild {Guild}", subCommand, guildId);
        
        return subCommand switch
        {
            "status" => await HandleStatusAsync(guildId),
            "enable" => await HandleEnableAsync(guildId, (bool)options["enabled"]),
            "alert-channel" => await HandleAlertChannelAsync(guildId, Convert.ToUInt64(options["channel"])),
            "min-channels" => await HandleMinChannelsAsync(guildId, Convert.ToInt32(options["count"])),
            "similarity" => await HandleSimilarityAsync(guildId, Convert.ToInt32(options["percent"])),
            "window" => await HandleWindowAsync(guildId, Convert.ToInt32(options["seconds"])),
            "mute" => await HandleMuteAsync(guildId, (bool)options["enabled"], 
                options.TryGetValue("duration", out var d) ? Convert.ToInt32(d) : 60),
            "delete" => await HandleDeleteAsync(guildId, (bool)options["enabled"]),
            _ => "❌ Unknown command"
        };
    }

    private async Task<string> HandleStatusAsync(ulong guildId)
    {
        var config = await _configService.GetOrCreateAsync(guildId);
        
        return $"""
            📊 **Anti-Spam Settings**
            
            Protection: {(config.IsEnabled ? "✅ Enabled" : "❌ Disabled")}
            Alert Channel: {(config.AlertChannelId.HasValue ? $"<#{config.AlertChannelId.Value}>" : "Not set")}
            
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
        await _configService.SetAlertChannelAsync(guildId, channelId);
        return $"✅ Alert channel set: <#{channelId}>";
    }

    private async Task<string> HandleMinChannelsAsync(ulong guildId, int count)
    {
        var success = await _configService.SetMinChannelsAsync(guildId, count);
        return success 
            ? $"✅ Minimum channels: {count}" 
            : "❌ Value must be between 2 and 10";
    }

    private async Task<string> HandleSimilarityAsync(ulong guildId, int percent)
    {
        var success = await _configService.SetSimilarityThresholdAsync(guildId, percent / 100.0);
        return success 
            ? $"✅ Similarity threshold: {percent}%" 
            : "❌ Value must be between 50 and 100";
    }

    private async Task<string> HandleWindowAsync(ulong guildId, int seconds)
    {
        var success = await _configService.SetDetectionWindowAsync(guildId, seconds);
        return success 
            ? $"✅ Detection window: {seconds} sec" 
            : "❌ Value must be between 30 and 600";
    }

    private async Task<string> HandleMuteAsync(ulong guildId, bool enabled, int duration)
    {
        var success = await _configService.SetMuteSettingsAsync(guildId, enabled, duration);
        return success 
            ? (enabled ? $"✅ Mute enabled: {duration} min" : "✅ Mute disabled") 
            : "❌ Duration must be between 1 and 1440 minutes";
    }

    private async Task<string> HandleDeleteAsync(ulong guildId, bool enabled)
    {
        await _configService.SetDeleteMessagesAsync(guildId, enabled);
        return enabled ? "✅ Message deletion enabled" : "✅ Message deletion disabled";
    }
}
