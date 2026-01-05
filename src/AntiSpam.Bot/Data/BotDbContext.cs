using AntiSpam.Bot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Data;

public class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }
    
    public DbSet<GuildConfig> GuildConfigs => Set<GuildConfig>();
    public DbSet<SpamIncident> SpamIncidents => Set<SpamIncident>();
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuildConfig>(entity =>
        {
            entity.HasKey(e => e.GuildId);
            
            // ulong хранится как decimal в PostgreSQL
            entity.Property(e => e.GuildId)
                .HasConversion<decimal>();
            
            entity.Property(e => e.AlertChannelId)
                .HasConversion<decimal?>();
        });
        
        modelBuilder.Entity<SpamIncident>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            entity.Property(e => e.GuildId)
                .HasConversion<decimal>();
            
            entity.Property(e => e.UserId)
                .HasConversion<decimal>();
            
            entity.Property(e => e.HandledByUserId)
                .HasConversion<decimal?>();
            
            // Список каналов как JSON
            entity.Property(e => e.ChannelIds)
                .HasConversion(
                    v => string.Join(',', v.Select(id => id.ToString())),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)
                          .Select(s => ulong.Parse(s))
                          .ToList());
            
            entity.HasIndex(e => e.GuildId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.GuildId, e.Status });
        });
    }
}
