using System.Text.Json;
using StackExchange.Redis;

namespace AntiSpam.Bot.Features.SpamDetection;

public record CachedMessage(
    string Content,
    ulong ChannelId,
    ulong MessageId,
    long Timestamp
);

public class UserMessageCache
{
    private readonly IDatabase _redis;
    private readonly ILogger<UserMessageCache> _logger;
    
    private const int MaxMessages = 20;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public UserMessageCache(IConnectionMultiplexer redis, ILogger<UserMessageCache> logger)
    {
        _redis = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<int> AddMessageAsync(ulong guildId, ulong userId, CachedMessage message)
    {
        var key = GetKey(guildId, userId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = JsonSerializer.Serialize(message);
        var cutoff = now - (long)Window.TotalSeconds;

        // Lua script — атомарно за один round-trip
        const string script = """
            redis.call('ZADD', KEYS[1], ARGV[1], ARGV[2])
            redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[3])
            redis.call('ZREMRANGEBYRANK', KEYS[1], 0, -21)
            redis.call('EXPIRE', KEYS[1], 3600)
            return redis.call('ZCARD', KEYS[1])
        """;

        var result = await _redis.ScriptEvaluateAsync(script,
            new RedisKey[] { key },
            new RedisValue[] { now, json, cutoff });

        var count = (int)result;
        
        _logger.LogDebug("Added message for {Guild}:{User}, total: {Count}", guildId, userId, count);
        
        return count;
    }

    public async Task<CachedMessage[]> GetRecentMessagesAsync(ulong guildId, ulong userId)
    {
        var key = GetKey(guildId, userId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cutoff = now - (long)Window.TotalSeconds;

        var entries = await _redis.SortedSetRangeByScoreAsync(key, cutoff, now);

        return entries
            .Select(e => JsonSerializer.Deserialize<CachedMessage>(e!))
            .Where(m => m != null)
            .ToArray()!;
    }

    public async Task<int> GetMessageCountAsync(ulong guildId, ulong userId)
    {
        var key = GetKey(guildId, userId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cutoff = now - (long)Window.TotalSeconds;

        return (int)await _redis.SortedSetLengthAsync(key, cutoff, now);
    }

    private static string GetKey(ulong guildId, ulong userId) 
        => $"user:{guildId}:{userId}:messages";
}
