using System.Text.Json;
using AntiSpam.Bot.Domain.SpamDetection;
using StackExchange.Redis;

namespace AntiSpam.Bot.Infrastructure.Cache;

public class MessageRepository
{
    private readonly IDatabase _redis;
    private const int MaxMessagesPerUser = 50;

    public MessageRepository(IConnectionMultiplexer redis)
    {
        _redis = redis.GetDatabase();
    }

    public async Task AddAsync(ulong guildId, ulong userId, CachedMessage message, TimeSpan window)
    {
        var key = GetKey(guildId, userId);
        var json = JsonSerializer.Serialize(message);
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)window.TotalSeconds;

        const string script = """
            redis.call('ZADD', KEYS[1], ARGV[1], ARGV[2])
            redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[3])
            redis.call('ZREMRANGEBYRANK', KEYS[1], 0, -(ARGV[4]+1))
            redis.call('EXPIRE', KEYS[1], 3600)
        """;

        await _redis.ScriptEvaluateAsync(script,
            [key],
            [message.Timestamp, json, cutoff, MaxMessagesPerUser]);
    }

    public async Task<IReadOnlyList<CachedMessage>> GetInWindowAsync(ulong guildId, ulong userId, TimeSpan window)
    {
        var key = GetKey(guildId, userId);
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)window.TotalSeconds;

        var entries = await _redis.SortedSetRangeByScoreAsync(key, cutoff, double.PositiveInfinity);

        return entries
            .Select(e => JsonSerializer.Deserialize<CachedMessage>(e!))
            .Where(m => m != null)
            .ToList()!;
    }

    public async Task ClearAsync(ulong guildId, ulong userId)
    {
        var key = GetKey(guildId, userId);
        await _redis.KeyDeleteAsync(key);
    }
    
    public async Task<bool> TryClaimActionAsync(ulong guildId, ulong userId, TimeSpan cooldown)
    {
        var key = ClaimKey(guildId, userId);
        return await _redis.StringSetAsync(key, "1", cooldown, When.NotExists);
    }
    
    public async Task ResetSpamStateAsync(ulong guildId, ulong userId)
    {
        await _redis.KeyDeleteAsync(new RedisKey[] { GetKey(guildId, userId), ClaimKey(guildId, userId) });
    }

    private static string GetKey(ulong guildId, ulong userId)
        => $"messages:{guildId}:{userId}";

    private static string ClaimKey(ulong guildId, ulong userId)
        => $"spam_handled:{guildId}:{userId}";
}
