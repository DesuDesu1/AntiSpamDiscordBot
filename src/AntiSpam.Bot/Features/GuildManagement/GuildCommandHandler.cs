using AntiSpam.Bot.Services;
using System.Text.Json;
using Discord.Rest;

namespace AntiSpam.Bot.Features.GuildManagement;

public class GuildCommandHandler
{
    private const string HelpText = """
        **Anti-Spam Bot**

        I automatically catch two kinds of spam and can delete the messages, time the user out, and alert your moderators:
        - The same or near-identical message posted across several channels in a short window.
        - Brand-new members posting links before they have any history (common bot behaviour).

        **Getting started**
        1. `/antispam alert-channel` - pick a moderators-only channel for alerts.
        2. `/antispam status` - review the current settings (every server starts with sensible defaults).
        3. Tune anything below to taste.

        **Detection**
        `/antispam min-channels` - how many channels the same message must hit (2-10)
        `/antispam similarity` - how alike messages must be to count as spam (50-100%)
        `/antispam window` - time window for the check, in seconds (30-600)
        `/antispam new-user-threshold` - how long someone counts as "new" for the link check (1-168h)

        **Allowed links** (links new members may post freely)
        `/antispam list-links`, `/antispam allow-link`, `/antispam remove-link`
        Well-known sites (youtube, twitch, etc.) are allowed by default.

        **Actions**
        `/antispam mute` - time the offender out (on/off, duration in minutes)
        `/antispam delete` - delete the detected spam (on/off)
        `/antispam enable` - turn all protection on or off

        **Permissions I need**
        Grant my role: Moderate Members (timeout), Ban Members (the Ban button), Manage Messages (delete spam), and in the alert channel View Channel, Send Messages, Embed Links and Attach Files (to show the spam image).
        Drag my role high in the role list: Discord will not let me mute, ban, or delete messages for the server owner or anyone whose top role sits above mine - those actions silently fail.

        Using the `/antispam` commands themselves requires the **Manage Server** permission.

        **Privacy**
        Privacy policy: <https://github.com/DesuDesu1/AntiSpamDiscordBot/blob/main/PRIVACY.md>
        Flagged-spam records are auto-deleted after 7 days. For data-deletion requests or questions, contact @nanashi1725 on Discord or ddesuone@gmail.com.
        """;

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
            "help" => HelpText,
            "status" => await HandleStatusAsync(guildId),
            "enable" => await HandleEnableAsync(guildId, GetBool(options, "enabled")),
            "alert-channel" => await HandleAlertChannelAsync(guildId, GetULong(options, "channel")),
            "min-channels" => await HandleMinChannelsAsync(guildId, GetInt(options, "count")),
            "similarity" => await HandleSimilarityAsync(guildId, GetInt(options, "percent")),
            "window" => await HandleWindowAsync(guildId, GetInt(options, "seconds")),
            "mute" => await HandleMuteAsync(guildId, GetBool(options, "enabled"), 
                options.ContainsKey("duration") ? GetInt(options, "duration") : 60),
            "delete" => await HandleDeleteAsync(guildId, GetBool(options, "enabled")),
            "allow-link" => await HandleAllowLinkAsync(guildId, GetString(options, "link")),
            "remove-link" => await HandleRemoveLinkAsync(guildId, GetString(options, "link")),
            "list-links" => await HandleListLinksAsync(guildId),
            "new-user-threshold" => await HandleNewUserThresholdAsync(guildId, GetInt(options, "hours")),
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

    private static string GetString(Dictionary<string, object> options, string key)
    {
        if (!options.TryGetValue(key, out var value)) return "";
        
        return value switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
            string s => s,
            _ => value.ToString() ?? ""
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

        var allowedLinksDisplay = config.AllowedLinks.Count > 0 
            ? $"{config.AllowedLinks.Count} links" 
            : "None";
        
        return $"""
            📊 **Anti-Spam Settings**
            
            Protection: {(config.IsEnabled ? "✅ Enabled" : "❌ Disabled")}
            Alert Channel: {alertChannelDisplay}
            
            **Detection:**
            • Min channels: {config.MinChannelsForSpam}
            • Text similarity: {config.SimilarityThreshold:P0}
            • Time window: {config.DetectionWindowSeconds} sec
            
            **New User Links:**
            • Detection: {(config.DetectNewUserLinks ? "✅ Enabled" : "❌ Disabled")}
            • New user threshold: {config.NewUserHoursThreshold}h
            • Allowed links: {allowedLinksDisplay}
            
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

    private async Task<string> HandleAllowLinkAsync(ulong guildId, string link)
    {
        var (success, message) = await _configService.AddAllowedLinkAsync(guildId, link);
        return success ? $"✅ {message}" : $"❌ {message}";
    }

    private async Task<string> HandleRemoveLinkAsync(ulong guildId, string link)
    {
        var (success, message) = await _configService.RemoveAllowedLinkAsync(guildId, link);
        return success ? $"✅ {message}" : $"❌ {message}";
    }

    private async Task<string> HandleListLinksAsync(ulong guildId)
    {
        var links = await _configService.GetAllowedLinksAsync(guildId);
        
        if (links.Count == 0)
            return "📋 **Allowed Links**\n\nNo links allowed yet.\nUse `/antispam allow-link` to add one.";
        
        var list = string.Join("\n", links.Select(l => $"• `{l}`"));
        return $"📋 **Allowed Links** ({links.Count}/100)\n\n{list}";
    }

    private async Task<string> HandleNewUserThresholdAsync(ulong guildId, int hours)
    {
        var success = await _configService.SetNewUserThresholdAsync(guildId, hours);
        return success 
            ? $"✅ New user threshold set to {hours}h" 
            : "❌ Hours must be 1-168 (1h to 7 days)";
    }
}
