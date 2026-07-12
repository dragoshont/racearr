using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Racearr.Web.Migrations
{
    /// <inheritdoc />
    public partial class EngineCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "engine_counters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Loops = table.Column<long>(type: "INTEGER", nullable: false),
                    Incidents = table.Column<long>(type: "INTEGER", nullable: false),
                    RacesStarted = table.Column<long>(type: "INTEGER", nullable: false),
                    CandidatesGrabbed = table.Column<long>(type: "INTEGER", nullable: false),
                    LosersKilled = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engine_counters", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "engine_counters");
        }
    }
}
