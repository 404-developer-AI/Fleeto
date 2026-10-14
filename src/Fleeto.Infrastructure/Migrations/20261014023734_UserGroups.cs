using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UserGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAt",
                table: "RemoteSessionParticipants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AllClients = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_UserGroupMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupMembers_UserGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserGroupRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupRules", x => x.Id);
                    table.CheckConstraint("CK_UserGroupRules_Target", "num_nonnulls(\"ClientId\", \"SiteId\", \"EndpointId\") = 1");
                    table.ForeignKey(
                        name: "FK_UserGroupRules_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupRules_Endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "Endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupRules_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupRules_UserGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupMembers_UserId",
                table: "UserGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupRules_ClientId",
                table: "UserGroupRules",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupRules_EndpointId",
                table: "UserGroupRules",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupRules_GroupId_Kind_ClientId_SiteId_EndpointId",
                table: "UserGroupRules",
                columns: new[] { "GroupId", "Kind", "ClientId", "SiteId", "EndpointId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupRules_SiteId",
                table: "UserGroupRules",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroups_IsDefault",
                table: "UserGroups",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroups_NormalizedName",
                table: "UserGroups",
                column: "NormalizedName",
                unique: true);

            // Nobody gains or loses access with the update: users that are not admins saw every client before 0.6.0, so they
            // all go into the default group "All clients". Admins are outside groups and see everything anyway.
            migrationBuilder.Sql("""
                INSERT INTO "UserGroups" ("Id", "Name", "NormalizedName", "Description", "AllClients", "IsDefault", "CreatedAt", "UpdatedAt")
                VALUES ('8d1f5a0e-6c4b-4e7a-9d35-2f6a1c0b7e41', 'All clients', 'ALL CLIENTS',
                        'Every client, also the ones created later. A user added to another group leaves this one.', TRUE, TRUE,
                        now() AT TIME ZONE 'utc', now() AT TIME ZONE 'utc');
                INSERT INTO "UserGroupMembers" ("GroupId", "UserId", "AddedAt")
                SELECT '8d1f5a0e-6c4b-4e7a-9d35-2f6a1c0b7e41', u."Id", now() AT TIME ZONE 'utc'
                FROM "AspNetUsers" u
                WHERE NOT EXISTS (SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                                  WHERE ur."UserId" = u."Id" AND r."NormalizedName" = 'ADMIN');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserGroupMembers");

            migrationBuilder.DropTable(
                name: "UserGroupRules");

            migrationBuilder.DropTable(
                name: "UserGroups");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "RemoteSessionParticipants");
        }
    }
}
