using System.Text.Json;
using AntiSpam.Bot.Domain.GuildManagement;
using AntiSpam.Bot.Domain.SpamIncident;
using AntiSpam.Bot.Infrastructure;
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
            entity.ToTable("GuildConfigs");
            entity.HasKey(e => e.GuildId);

            // ulong stored as decimal in PostgreSQL
            entity.Property(e => e.GuildId)
                .HasConversion<decimal>();

            entity.Property(e => e.AlertChannelId)
                .HasConversion<decimal?>();

            // AllowedLinks is a read-only projection over a private backing field (behavior lives
            // on the aggregate - AllowLink/RemoveLink - not on this property), so EF needs to be
            // told explicitly to materialize through the field instead of a (non-existent) setter.
            var linksComparer = new ValueComparer<IReadOnlyList<string>>(
                (c1, c2) => c1!.SequenceEqual(c2!),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());

            entity.Property(e => e.AllowedLinks)
                .HasField("_allowedLinks")
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasConversion(
                    v => string.Join(',', v),
                    v => string.IsNullOrEmpty(v)
                        ? new List<string>()
                        : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(linksComparer);

            // Allow-listed external invite servers (id + display name). Stored as JSON rather than
            // CSV because server names are free text (commas/colons would break a delimiter).
            var inviteServersComparer = new ValueComparer<IReadOnlyList<AllowedInviteServer>>(
                (c1, c2) => c1!.SequenceEqual(c2!),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());

            entity.Property(e => e.AllowedInviteServers)
                .HasField("_allowedInviteServers")
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v)
                        ? new List<AllowedInviteServer>()
                        : JsonSerializer.Deserialize<List<AllowedInviteServer>>(v, (JsonSerializerOptions?)null)!)
                .Metadata.SetValueComparer(inviteServersComparer);
        });

        modelBuilder.Entity<SpamIncident>(entity =>
        {
            entity.ToTable("SpamIncidents");
            entity.HasKey(e => e.Id);

            // Encrypt the stored message content at rest (Discord developer policy).
            entity.Property(e => e.Content)
                .HasConversion(
                    v => ContentCipher.Encrypt(v),
                    v => ContentCipher.Decrypt(v));

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

            var channelIdsComparer = new ValueComparer<IReadOnlyList<ulong>>(
                (c1, c2) => c1!.SequenceEqual(c2!),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList());

            entity.Property(e => e.ChannelIds)
                .HasField("_channelIds")
                .UsePropertyAccessMode(PropertyAccessMode.Field)
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
