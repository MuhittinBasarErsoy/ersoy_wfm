using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wfm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskSurveys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultSurveyId",
                table: "TaskTypes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SurveyId",
                table: "Tasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SurveyResponseCount",
                table: "Tasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SurveyTargetCount",
                table: "Tasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SurveyVersionId",
                table: "Tasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "SurveyVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "SurveyResponses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurveyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurveyVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    RespondentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RespondentPhone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConsentGiven = table.Column<bool>(type: "bit", nullable: false),
                    Answers = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SurveyResponses_SurveyVersions_SurveyVersionId",
                        column: x => x.SurveyVersionId,
                        principalTable: "SurveyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurveyResponses_Tasks_WorkTaskId",
                        column: x => x.WorkTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SurveyVersionId",
                table: "Tasks",
                column: "SurveyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponses_SurveyId_SubmittedAt",
                table: "SurveyResponses",
                columns: new[] { "SurveyId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponses_SurveyVersionId",
                table: "SurveyResponses",
                column: "SurveyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponses_WorkTaskId_ClientId",
                table: "SurveyResponses",
                columns: new[] { "WorkTaskId", "ClientId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_SurveyVersions_SurveyVersionId",
                table: "Tasks",
                column: "SurveyVersionId",
                principalTable: "SurveyVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Daha önce yayımlanmış sürümlerin başlığı anketten kopyalanır.
            migrationBuilder.Sql("UPDATE v SET v.Title = s.Title FROM SurveyVersions v INNER JOIN Surveys s ON s.Id = v.SurveyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_SurveyVersions_SurveyVersionId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "SurveyResponses");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_SurveyVersionId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DefaultSurveyId",
                table: "TaskTypes");

            migrationBuilder.DropColumn(
                name: "SurveyId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "SurveyResponseCount",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "SurveyTargetCount",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "SurveyVersionId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "SurveyVersions");
        }
    }
}
