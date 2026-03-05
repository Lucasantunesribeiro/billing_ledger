using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BillingLedger.Payments.Worker.Migrations
{
    /// <inheritdoc />
    public partial class InitialPaymentsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "payment_attempts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_payment_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_invoice_id",
                schema: "payments",
                table: "payment_attempts",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "uq_payment_attempts_provider_external_id",
                schema: "payments",
                table: "payment_attempts",
                columns: new[] { "provider", "external_payment_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_attempts",
                schema: "payments");
        }
    }
}
