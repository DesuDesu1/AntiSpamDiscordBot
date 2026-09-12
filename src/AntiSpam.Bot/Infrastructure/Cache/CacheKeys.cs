namespace AntiSpam.Bot.Infrastructure.Cache;

public static class CacheKeys
{
    public static string GuildConfig(ulong guildId) => $"guild_config:{guildId}";

    public static string Channel(ulong channelId) => $"channel:{channelId}";
}
