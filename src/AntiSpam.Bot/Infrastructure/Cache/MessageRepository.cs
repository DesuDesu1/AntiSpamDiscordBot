using System.Text.Json;
using AntiSpam.Bot.Domain.SpamDetection;
using StackExchange.Redis;

namespace AntiSpam.Bot.Infrastructure.Cache;

/// <summary>
/// Redis-backed sliding window of a user's recent messages, plus the burst-collapse claim lock.
/// </summary>
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
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = JsonSerializer.Serialize(message);
        var cutoff = now - (long)window.TotalSeconds;

        const string script = """
            redis.call('ZADD', KEYS[1], ARGV[1], ARGV[2])
            redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[3])
            redis.call('ZREMRANGEBYRANK', KEYS[1], 0, -(ARGV[4]+1))
            redis.call('EXPIRE', KEYS[1], 3600)
        """;

        await _redis.ScriptEvaluateAsync(script,
            new RedisKey[] { key },
            new RedisValue[] { now, json, cutoff, MaxMessagesPerUser });
    }

    public async Task<IReadOnlyList<CachedMessage>> GetInWindowAsync(ulong guildId, ulong userId, TimeSpan window)
    {
        var key = GetKey(guildId, userId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cutoff = now - (long)window.TotalSeconds;

        var entries = await _redis.SortedSetRangeByScoreAsync(key, cutoff, now);

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

    /// <summary>
    /// Atomically claims the right to act on this user once per cooldown window.
    /// Returns true only for the first caller; later messages from the same spam burst
    /// (and any Kafka redelivery or second bot replica) get false and skip acting,
    /// so one burst produces a single alert instead of one per message.
    /// </summary>
    public async Task<bool> TryClaimActionAsync(ulong guildId, ulong userId, TimeSpan cooldown)
    {
        var key = ClaimKey(guildId, userId);
        return await _redis.StringSetAsync(key, "1", cooldown, When.NotExists);
    }

    /// <summary>
    /// Clears a user's cached messages and the action-claim cooldown after a moderator handles
    /// an incident. On release this stops the leftover window from instantly re-flagging the user,
    /// and lets a genuinely renewed spam burst raise a fresh alert.
    /// </summary>
    public async Task ResetSpamStateAsync(ulong guildId, ulong userId)
    {
        await _redis.KeyDeleteAsync(new RedisKey[] { GetKey(guildId, userId), ClaimKey(guildId, userId) });
    }

    private static string GetKey(ulong guildId, ulong userId)
        => $"messages:{guildId}:{userId}";

    private static string ClaimKey(ulong guildId, ulong userId)
        => $"spam_handled:{guildId}:{userId}";
}
