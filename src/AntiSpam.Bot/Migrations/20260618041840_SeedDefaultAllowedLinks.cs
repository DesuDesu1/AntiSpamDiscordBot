using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AntiSpam.Bot.Migrations
{
    /// <inheritdoc />
    public partial class SeedDefaultAllowedLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time merge: append the default allowed-links template to every existing guild,
            // keeping each guild's own links and their order, and skipping defaults already present.
            // New guilds get these from the entity initializer; this backfills servers created before then.
            migrationBuilder.Sql("""
                UPDATE "GuildConfigs" AS g
                SET "AllowedLinks" = trim(BOTH ',' FROM concat_ws(',',
                    NULLIF(g."AllowedLinks", ''),
                    (SELECT string_agg(d, ',')
                     FROM unnest(ARRAY[
                         'youtube.com','youtu.be','twitch.tv','tenor.com','giphy.com',
                         'imgur.com','reddit.com','twitter.com','x.com','spotify.com',
                         'soundcloud.com','github.com','wikipedia.org'
                     ]) AS d
                     WHERE lower(d) <> ALL (string_to_array(lower(g."AllowedLinks"), ',')))
                ));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: once merged, a guild's defaults are indistinguishable from links it added itself,
            // so there is nothing safe to remove on rollback.
        }
    }
}
