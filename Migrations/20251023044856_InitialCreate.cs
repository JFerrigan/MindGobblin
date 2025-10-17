using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JakeServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PongGameLobbies",
                columns: table => new
                {
                    LobbyId = table.Column<string>(type: "TEXT", nullable: false),
                    LobbyName = table.Column<string>(type: "TEXT", nullable: true),
                    HostConnectionId = table.Column<string>(type: "TEXT", nullable: true),
                    ChallengerConnectionId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PongGameLobbies", x => x.LobbyId);
                });

            migrationBuilder.CreateTable(
                name: "TetrisScores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Player = table.Column<string>(type: "TEXT", nullable: false),
                    Points = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TetrisScores", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PongGameLobbies");

            migrationBuilder.DropTable(
                name: "TetrisScores");
        }
    }
}
