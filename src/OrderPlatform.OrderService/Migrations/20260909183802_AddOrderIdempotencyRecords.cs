using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderPlatform.OrderService.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_idempotency_records",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    final_status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_idempotency_records", x => x.idempotency_key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_idempotency_records");
        }
    }
}
