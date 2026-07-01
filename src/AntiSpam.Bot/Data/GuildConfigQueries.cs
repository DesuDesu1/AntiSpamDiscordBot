using AntiSpam.Bot.Domain.GuildManagement;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Data;

public static class GuildConfigQueries
{
    public static async Task<GuildConfig> GetOrCreateAsync(this DbSet<GuildConfig> configs, ulong guildId, CancellationToken ct)
        => await configs.FirstOrDefaultAsync(c => c.GuildId == guildId, ct)
           ?? configs.Add(GuildConfig.CreateDefault(guildId)).Entity;
    
    public static async Task<GuildConfig> GetOrDefaultAsync(this IQueryable<GuildConfig> configs, ulong guildId, CancellationToken ct)
        => await configs.FirstOrDefaultAsync(c => c.GuildId == guildId, ct)
           ?? GuildConfig.CreateDefault(guildId);
}
