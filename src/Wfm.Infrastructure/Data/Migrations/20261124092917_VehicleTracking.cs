using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wfm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VehicleTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsVehicleLocation",
                table: "LocationPings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VehiclePlate",
                table: "LocationPings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleLabel",
                table: "AspNetUsers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehiclePlate",
                table: "AspNetUsers",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VehicleTrackingEnabled",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "VehicleTrackingEnabledAt",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VehicleObserverAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AllVehicles = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleObserverAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleObserverAccesses_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleObserverVehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObserverId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleObserverVehicles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleObserverVehicles_AspNetUsers_VehicleUserId",
                        column: x => x.VehicleUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocationPings_TenantId_IsVehicleLocation_UserId_RecordedAt",
                table: "LocationPings",
                columns: new[] { "TenantId", "IsVehicleLocation", "UserId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleObserverAccesses_TenantId_UserId",
                table: "VehicleObserverAccesses",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleObserverAccesses_UserId",
                table: "VehicleObserverAccesses",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleObserverVehicles_TenantId_ObserverId_VehicleUserId",
                table: "VehicleObserverVehicles",
                columns: new[] { "TenantId", "ObserverId", "VehicleUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleObserverVehicles_VehicleUserId",
                table: "VehicleObserverVehicles",
                column: "VehicleUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VehicleObserverAccesses");

            migrationBuilder.DropTable(
                name: "VehicleObserverVehicles");

            migrationBuilder.DropIndex(
                name: "IX_LocationPings_TenantId_IsVehicleLocation_UserId_RecordedAt",
                table: "LocationPings");

            migrationBuilder.DropColumn(
                name: "IsVehicleLocation",
                table: "LocationPings");

            migrationBuilder.DropColumn(
                name: "VehiclePlate",
                table: "LocationPings");

            migrationBuilder.DropColumn(
                name: "VehicleLabel",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "VehiclePlate",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "VehicleTrackingEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "VehicleTrackingEnabledAt",
                table: "AspNetUsers");
        }
    }
}
