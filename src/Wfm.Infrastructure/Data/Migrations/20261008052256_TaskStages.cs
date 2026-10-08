using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wfm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresVisit",
                table: "TaskTypes",
                type: "bit",
                nullable: false,
                defaultValue: true); // mevcut tipler saha ziyaretli kalır

            migrationBuilder.AddColumn<string>(
                name: "Stages",
                table: "TaskTypes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "Tasks",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "TaskEvents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresVisit",
                table: "TaskTypes");

            migrationBuilder.DropColumn(
                name: "Stages",
                table: "TaskTypes");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "TaskEvents");
        }
    }
}
