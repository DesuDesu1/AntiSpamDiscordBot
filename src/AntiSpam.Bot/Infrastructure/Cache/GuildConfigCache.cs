using System.Text.Json;
using AntiSpam.Bot.Domain.GuildManagement;
using StackExchange.Redis;

namespace AntiSpam.Bot.Infrastructure.Cache;

public sealed class GuildConfigCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly IDatabase _redis;
    
    public GuildConfigCache(IConnectionMultiplexer redis)
    {
        _redis = redis.GetDatabase();
    }

    private static string Key(ulong guildId) => $"guild_config:{guildId}";

    public async Task<GuildConfig?> TryGetAsync(ulong guildId)
    {
        var cached = await _redis.StringGetAsync(Key(guildId));
        if (!cached.HasValue)
            return null;

        var snapshot = JsonSerializer.Deserialize<GuildConfigSnapshot>(cached!);
        return snapshot == null ? null : GuildConfig.FromSnapshot(snapshot);
    }

    public Task SetAsync(GuildConfig config) =>
        _redis.StringSetAsync(Key(config.GuildId), JsonSerializer.Serialize(config.ToSnapshot()), Ttl);

    public Task InvalidateAsync(ulong guildId) => _redis.KeyDeleteAsync(Key(guildId));
}
