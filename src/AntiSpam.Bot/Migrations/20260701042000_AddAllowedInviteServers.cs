using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AntiSpam.Bot.Migrations
{
    /// <inheritdoc />
    public partial class AddAllowedInviteServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedInviteServers",
                table: "GuildConfigs",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedInviteServers",
                table: "GuildConfigs");
        }
    }
}
