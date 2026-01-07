using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AntiSpam.Bot.Migrations
{
    /// <inheritdoc />
    public partial class RenameNewUserThresholdToHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildConfigs",
                columns: table => new
                {
                    GuildId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    AlertChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MinChannelsForSpam = table.Column<int>(type: "integer", nullable: false),
                    DetectionWindowSeconds = table.Column<int>(type: "integer", nullable: false),
                    SimilarityThreshold = table.Column<double>(type: "double precision", nullable: false),
                    DeleteMessages = table.Column<bool>(type: "boolean", nullable: false),
                    MuteOnSpam = table.Column<bool>(type: "boolean", nullable: false),
                    MuteDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    DetectNewUserLinks = table.Column<bool>(type: "boolean", nullable: false),
                    NewUserHoursThreshold = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildConfigs", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "SpamIncidents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GuildId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    UserId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ChannelIds = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AlertMessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    AlertChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    HandledByUserId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    HandledByUsername = table.Column<string>(type: "text", nullable: true),
                    ModeratorNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HandledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpamIncidents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpamIncidents_AlertMessageId",
                table: "SpamIncidents",
                column: "AlertMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SpamIncidents_GuildId",
                table: "SpamIncidents",
                column: "GuildId");

            migrationBuilder.CreateIndex(
                name: "IX_SpamIncidents_GuildId_Status",
                table: "SpamIncidents",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SpamIncidents_Status",
                table: "SpamIncidents",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildConfigs");

            migrationBuilder.DropTable(
                name: "SpamIncidents");
        }
    }
}
