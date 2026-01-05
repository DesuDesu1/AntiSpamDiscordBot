using AntiSpam.Bot.Data;
using AntiSpam.Bot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.GuildManagement;

public class GuildConfigService
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;

    public GuildConfigService(IDbContextFactory<BotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<GuildConfig?> GetAsync(ulong guildId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.GuildConfigs.FindAsync(guildId);
    }

    public async Task<GuildConfig> GetOrCreateAsync(ulong guildId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        
        var config = await db.GuildConfigs.FindAsync(guildId);
        if (config != null)
            return config;

        config = new GuildConfig { GuildId = guildId };
        db.GuildConfigs.Add(config);
        await db.SaveChangesAsync();
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
}
