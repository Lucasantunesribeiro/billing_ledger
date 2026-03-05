using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BillingLedger.Ledger.Worker.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedgerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_invoice_id",
                schema: "ledger",
                table: "ledger_entries",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "uq_ledger_entries_event_id",
                schema: "ledger",
                table: "ledger_entries",
                column: "event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledger_entries",
                schema: "ledger");
        }
    }
}
