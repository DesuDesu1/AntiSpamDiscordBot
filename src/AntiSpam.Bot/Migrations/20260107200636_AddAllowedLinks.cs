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
            migrationBuilder.AddColumn<string>(
                name: "AllowedLinks",
                table: "GuildConfigs",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedLinks",
                table: "GuildConfigs");
        }
    }
}
