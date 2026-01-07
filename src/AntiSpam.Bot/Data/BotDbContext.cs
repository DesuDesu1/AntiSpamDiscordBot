using AntiSpam.Bot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
            
            // ulong stored as decimal in PostgreSQL
            entity.Property(e => e.GuildId)
                .HasConversion<decimal>();
            
            entity.Property(e => e.AlertChannelId)
                .HasConversion<decimal?>();
            
            // Allowed links as comma-separated string
            var linksComparer = new ValueComparer<List<string>>(
                (c1, c2) => c1!.SequenceEqual(c2!),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());
            
            entity.Property(e => e.AllowedLinks)
                .HasConversion(
                    v => string.Join(',', v),
                    v => string.IsNullOrEmpty(v) 
                        ? new List<string>() 
                        : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(linksComparer);
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
            
            entity.Property(e => e.AlertMessageId)
                .HasConversion<decimal?>();
            
            entity.Property(e => e.AlertChannelId)
                .HasConversion<decimal?>();
            
            // Channel IDs as comma-separated string with ValueComparer
            var channelIdsComparer = new ValueComparer<List<ulong>>(
                (c1, c2) => c1!.SequenceEqual(c2!),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());
            
            entity.Property(e => e.ChannelIds)
                .HasConversion(
                    v => string.Join(',', v.Select(id => id.ToString())),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)
                          .Select(s => ulong.Parse(s))
                          .ToList())
                .Metadata.SetValueComparer(channelIdsComparer);
            
            entity.HasIndex(e => e.GuildId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.GuildId, e.Status });
            entity.HasIndex(e => e.AlertMessageId);
        });
    }
}
