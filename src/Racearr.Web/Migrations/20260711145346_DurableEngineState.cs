using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Racearr.Web.Migrations
{
    /// <inheritdoc />
    public partial class DurableEngineState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "engine_item_states",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Instance = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    PickupFirstSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PickupAlerted = table.Column<bool>(type: "INTEGER", nullable: false),
                    QueueFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    QueueFirstSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRetryUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastIncidentType = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engine_item_states", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_engine_item_states_UpdatedUtc",
                table: "engine_item_states",
                column: "UpdatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "engine_item_states");
        }
    }
}
