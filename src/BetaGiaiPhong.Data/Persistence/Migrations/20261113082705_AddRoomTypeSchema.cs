using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetaGiaiPhong.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomTypeSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "room_type",
                schema: "dbo",
                table: "Rooms",
                type: "varchar(24)",
                nullable: false,
                defaultValue: "2D");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Rooms_02",
                schema: "dbo",
                table: "Rooms",
                sql: "(room_type IN ('2D','3D','IMAX','IMAX_3D','4DX','SCREENX','DOLBY_ATMOS','VIP'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Rooms_02",
                schema: "dbo",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "room_type",
                schema: "dbo",
                table: "Rooms");
        }
    }
}
