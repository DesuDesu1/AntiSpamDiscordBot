using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AntiSpam.Bot.Migrations
{
    /// <inheritdoc />
    public partial class AddAllowedLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AllowedDomains",
                table: "GuildConfigs",
                newName: "AllowedLinks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AllowedLinks",
                table: "GuildConfigs",
                newName: "AllowedDomains");
        }
    }
}
