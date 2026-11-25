using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wfm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VehicleObserverScheduleAndSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SessionDurationMinutes",
                table: "VehicleObserverAccesses",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "WeeklyScheduleJson",
                table: "VehicleObserverAccesses",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VehicleObserverSessionId",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VehicleObserverSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleObserverSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleObserverSessions_TenantId_UserId_CreatedAt",
                table: "VehicleObserverSessions",
                columns: new[] { "TenantId", "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VehicleObserverSessions");

            migrationBuilder.DropColumn(
                name: "SessionDurationMinutes",
                table: "VehicleObserverAccesses");

            migrationBuilder.DropColumn(
                name: "WeeklyScheduleJson",
                table: "VehicleObserverAccesses");

            migrationBuilder.DropColumn(
                name: "VehicleObserverSessionId",
                table: "RefreshTokens");
        }
    }
}
