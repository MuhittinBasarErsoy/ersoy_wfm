using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wfm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Patrols : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatrolRounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EnforceOrder = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Checkpoints = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartReminderSent = table.Column<bool>(type: "bit", nullable: false),
                    EndReminderSent = table.Column<bool>(type: "bit", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolRounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PatrolRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EnforceOrder = table.Column<bool>(type: "bit", nullable: false),
                    Days = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Windows = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReminderMinutesBeforeEnd = table.Column<int>(type: "int", nullable: false),
                    Checkpoints = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolRoutes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PatrolVisits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoundId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    Accuracy = table.Column<double>(type: "float", nullable: true),
                    DistanceMeters = table.Column<double>(type: "float", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolVisits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolVisits_PatrolRounds_RoundId",
                        column: x => x.RoundId,
                        principalTable: "PatrolRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRounds_AssigneeId_WindowEnd",
                table: "PatrolRounds",
                columns: new[] { "AssigneeId", "WindowEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRounds_RouteId_WindowStart",
                table: "PatrolRounds",
                columns: new[] { "RouteId", "WindowStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRounds_Status_WindowEnd",
                table: "PatrolRounds",
                columns: new[] { "Status", "WindowEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRounds_TenantId_WindowStart",
                table: "PatrolRounds",
                columns: new[] { "TenantId", "WindowStart" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolVisits_ClientId",
                table: "PatrolVisits",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolVisits_RoundId_CheckpointId",
                table: "PatrolVisits",
                columns: new[] { "RoundId", "CheckpointId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatrolRoutes");

            migrationBuilder.DropTable(
                name: "PatrolVisits");

            migrationBuilder.DropTable(
                name: "PatrolRounds");
        }
    }
}
