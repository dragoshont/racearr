using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Racearr.Web.Migrations;

[DbContext(typeof(RacearrDbContext))]
[Migration("20260810190000_TerminalRaceAttemptBudget")]
public partial class TerminalRaceAttemptBudget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE engine_item_states SET RetryCount = 0, NextRetryUtc = NULL;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
