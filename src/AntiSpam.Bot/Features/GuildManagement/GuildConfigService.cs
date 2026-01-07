using AntiSpam.Bot.Data;
using AntiSpam.Bot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.Json;

namespace AntiSpam.Bot.Features.GuildManagement;

public class GuildConfigService
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly IDatabase _redis;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public GuildConfigService(IDbContextFactory<BotDbContext> dbFactory, IConnectionMultiplexer redis)
    {
        _dbFactory = dbFactory;
        _redis = redis.GetDatabase();
    }

    private static string CacheKey(ulong guildId) => $"guild_config:{guildId}";

    public async Task<GuildConfig?> GetAsync(ulong guildId)
    {
        var key = CacheKey(guildId);
        
        var cached = await _redis.StringGetAsync(key);
        if (cached.HasValue)
            return JsonSerializer.Deserialize<GuildConfig>(cached!);
        
        await using var db = await _dbFactory.CreateDbContextAsync();
        var config = await db.GuildConfigs.FindAsync(guildId);
        
        if (config != null)
            await _redis.StringSetAsync(key, JsonSerializer.Serialize(config), CacheDuration);
        
        return config;
    }

    public async Task<GuildConfig> GetOrCreateAsync(ulong guildId)
    {
        var config = await GetAsync(guildId);
        if (config != null)
            return config;

        await using var db = await _dbFactory.CreateDbContextAsync();
        config = new GuildConfig { GuildId = guildId };
        db.GuildConfigs.Add(config);
        await db.SaveChangesAsync();
        
        await _redis.StringSetAsync(CacheKey(guildId), JsonSerializer.Serialize(config), CacheDuration);
        return config;
    }

    public async Task<GuildConfig> UpdateAsync(ulong guildId, Action<GuildConfig> update)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        
        var config = await db.GuildConfigs.FindAsync(guildId);
        if (config == null)
        {
            config = new GuildConfig { GuildId = guildId };
            db.GuildConfigs.Add(config);
        }

        update(config);
        config.UpdatedAt = DateTime.UtcNow;
        
        await db.SaveChangesAsync();
        
        // Invalidate cache
        await _redis.KeyDeleteAsync(CacheKey(guildId));
        
        return config;
    }

    public async Task<bool> SetAlertChannelAsync(ulong guildId, ulong channelId)
    {
        await UpdateAsync(guildId, c => c.AlertChannelId = channelId);
        return true;
    }

    public async Task<bool> SetEnabledAsync(ulong guildId, bool enabled)
    {
        await UpdateAsync(guildId, c => c.IsEnabled = enabled);
        return true;
    }

    public async Task<bool> SetMinChannelsAsync(ulong guildId, int minChannels)
    {
        if (minChannels < 2 || minChannels > 10)
            return false;
        
        await UpdateAsync(guildId, c => c.MinChannelsForSpam = minChannels);
        return true;
    }

    public async Task<bool> SetSimilarityThresholdAsync(ulong guildId, double threshold)
    {
        if (threshold < 0.5 || threshold > 1.0)
            return false;
        
        await UpdateAsync(guildId, c => c.SimilarityThreshold = threshold);
        return true;
    }

    public async Task<bool> SetDetectionWindowAsync(ulong guildId, int seconds)
    {
        if (seconds < 30 || seconds > 600)
            return false;
        
        await UpdateAsync(guildId, c => c.DetectionWindowSeconds = seconds);
        return true;
    }

    public async Task<bool> SetMuteSettingsAsync(ulong guildId, bool muteOnSpam, int muteDurationMinutes)
    {
        if (muteDurationMinutes < 1 || muteDurationMinutes > 1440)
            return false;
        
        await UpdateAsync(guildId, c =>
        {
            c.MuteOnSpam = muteOnSpam;
            c.MuteDurationMinutes = muteDurationMinutes;
        });
        return true;
    }

    public async Task<bool> SetDeleteMessagesAsync(ulong guildId, bool deleteMessages)
    {
        await UpdateAsync(guildId, c => c.DeleteMessages = deleteMessages);
        return true;
    }

    public async Task<bool> SetNewUserThresholdAsync(ulong guildId, int hours)
    {
        if (hours < 1 || hours > 168) // 1 hour to 7 days
            return false;
        
        await UpdateAsync(guildId, c => c.NewUserHoursThreshold = hours);
        return true;
    }

    public async Task<(bool Success, string Message)> AddAllowedLinkAsync(ulong guildId, string link)
    {
        link = NormalizeLink(link);
        
        if (string.IsNullOrWhiteSpace(link))
            return (false, "Invalid link");
        
        var config = await GetOrCreateAsync(guildId);
        
        if (config.AllowedLinks.Contains(link, StringComparer.OrdinalIgnoreCase))
            return (false, $"`{link}` is already in allowed list");
        
        if (config.AllowedLinks.Count >= 100)
            return (false, "Maximum 100 allowed links");
        
        await UpdateAsync(guildId, c => c.AllowedLinks.Add(link));
        return (true, $"Added `{link}` to allowed links");
    }

    public async Task<(bool Success, string Message)> RemoveAllowedLinkAsync(ulong guildId, string link)
    {
        link = NormalizeLink(link);
        
        var config = await GetAsync(guildId);
        if (config == null || !config.AllowedLinks.Contains(link, StringComparer.OrdinalIgnoreCase))
            return (false, $"`{link}` is not in allowed list");
        
        await UpdateAsync(guildId, c => 
            c.AllowedLinks.RemoveAll(l => l.Equals(link, StringComparison.OrdinalIgnoreCase)));
        return (true, $"Removed `{link}` from allowed links");
    }

    public async Task<List<string>> GetAllowedLinksAsync(ulong guildId)
    {
        var config = await GetAsync(guildId);
        return config?.AllowedLinks ?? new List<string>();
    }

    private static string NormalizeLink(string link)
    {
        link = link.Trim().ToLowerInvariant();
        
        // Remove protocol if present
        if (link.StartsWith("https://"))
            link = link[8..];
        else if (link.StartsWith("http://"))
            link = link[7..];
        
        // Remove trailing slash
        link = link.TrimEnd('/');
        
        return link;
    }
}
