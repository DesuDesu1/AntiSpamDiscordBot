using System.Text.Json;
using StackExchange.Redis;

namespace AntiSpam.Bot.Services.Cache;

/// <summary>
/// Repository для хранения сообщений пользователей в Redis
/// </summary>
public class MessageRepository
{
    private readonly IDatabase _redis;
    private const int MaxMessagesPerUser = 50;

    public MessageRepository(IConnectionMultiplexer redis)
    {
        _redis = redis.GetDatabase();
    }

    /// <summary>
    /// Добавляет сообщение в кэш пользователя
    /// </summary>
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

    /// <summary>
    /// Получает сообщения пользователя за указанный период
    /// </summary>
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

    /// <summary>
    /// Удаляет все сообщения пользователя в гильдии
    /// </summary>
    public async Task ClearAsync(ulong guildId, ulong userId)
    {
        var key = GetKey(guildId, userId);
        await _redis.KeyDeleteAsync(key);
    }

    private static string GetKey(ulong guildId, ulong userId) 
        => $"messages:{guildId}:{userId}";
}
