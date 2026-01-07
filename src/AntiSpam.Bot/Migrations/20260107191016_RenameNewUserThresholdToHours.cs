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
            // Add new columns for new user link detection feature
            migrationBuilder.AddColumn<bool>(
                name: "DetectNewUserLinks",
                table: "GuildConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "NewUserHoursThreshold",
                table: "GuildConfigs",
                type: "integer",
                nullable: false,
                defaultValue: 24);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectNewUserLinks",
                table: "GuildConfigs");

            migrationBuilder.DropColumn(
                name: "NewUserHoursThreshold",
                table: "GuildConfigs");
        }
    }
}
