using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wfm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocationPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireLocationPhoto",
                table: "Tasks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CaptureAddress",
                table: "Attachments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocationPhoto",
                table: "Attachments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "LocationAccuracy",
                table: "Attachments",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireLocationPhoto",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CaptureAddress",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "IsLocationPhoto",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "LocationAccuracy",
                table: "Attachments");
        }
    }
}
