using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <summary>
    /// Remote sessions (0.3.0): the session, one row per technician's connection with its signed token, the actions of a remote background
    /// session, and the remote session settings of a policy. Additive: the previous release keeps running, because every new policy column
    /// has a database default. The booleans keep that default only in the database (the model writes them always). Web may request remote
    /// session tokens (TR_SigningRequests_Origin).
    /// </summary>
    public partial class RemoteSessions : Migration
    {
        /// <summary>The rule as of 0.3.0: every <c>SigningRequestKind</c> with the roles that may create it.</summary>
        internal const string OriginFunctionSql = """
            CREATE OR REPLACE FUNCTION fleeto_signing_request_origin() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."Kind" IN ('AgentEnrollment', 'AgentRenewal', 'GatewayCertificate', 'AgentRecovery', 'WatchdogCertificate') THEN
                IF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer') THEN
                  RAISE EXCEPTION '% requests may only be created by the gateway', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF NEW."Kind" = 'AgentConfig' THEN
                IF current_user IN ('fleeto_web', 'fleeto_gateway') THEN
                  RAISE EXCEPTION 'AgentConfig requests may only be created by the workers or the signer' USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF NEW."Kind" IN ('Job', 'RemoteSessionToken') THEN
                IF current_user IN ('fleeto_gateway', 'fleeto_workers', 'fleeto_signer') THEN
                  RAISE EXCEPTION '% requests may only be created by web', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
                END IF;
              ELSIF current_user IN ('fleeto_web', 'fleeto_workers', 'fleeto_signer', 'fleeto_gateway') THEN
                RAISE EXCEPTION 'Unknown signing request kind %', NEW."Kind" USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(OriginFunctionSql);

            migrationBuilder.AddColumn<bool>(
                name: "RemoteBannerVisible",
                table: "Policies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemoteClipboardEnabled",
                table: "Policies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemoteConsentRequired",
                table: "Policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RemoteConsentTimeoutSeconds",
                table: "Policies",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "RemoteIdleTimeoutMinutes",
                table: "Policies",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<long>(
                name: "RemoteMaxFileBytes",
                table: "Policies",
                type: "bigint",
                nullable: false,
                defaultValue: 10737418240L);

            migrationBuilder.CreateTable(
                name: "RemoteSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSessions", x => x.Id);
                    table.UniqueConstraint("AK_RemoteSessions_Id_ClientId", x => new { x.Id, x.ClientId });
                    table.ForeignKey(
                        name: "FK_RemoteSessions_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSessionParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BrowserPublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    TokenPayload = table.Column<byte[]>(type: "bytea", nullable: true),
                    TokenSignature = table.Column<byte[]>(type: "bytea", nullable: true),
                    SigningKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectingAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSessionParticipants", x => x.Id);
                    table.CheckConstraint("CK_RemoteSessionParticipants_BrowserKey", "octet_length(\"BrowserPublicKey\") = 32");
                    table.CheckConstraint("CK_RemoteSessionParticipants_Signed", "\"ConnectingAt\" IS NULL OR \"TokenSignature\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_RemoteSessionParticipants_Endpoints_EndpointId_ClientId",
                        columns: x => new { x.EndpointId, x.ClientId },
                        principalTable: "Endpoints",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RemoteSessionParticipants_RemoteSessions_SessionId_ClientId",
                        columns: x => new { x.SessionId, x.ClientId },
                        principalTable: "RemoteSessions",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSessionActions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Target = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSessionActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteSessionActions_RemoteSessionParticipants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "RemoteSessionParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RemoteSessionActions_RemoteSessions_SessionId_ClientId",
                        columns: x => new { x.SessionId, x.ClientId },
                        principalTable: "RemoteSessions",
                        principalColumns: new[] { "Id", "ClientId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Policies_RemoteConsentTimeoutSeconds",
                table: "Policies",
                sql: "\"RemoteConsentTimeoutSeconds\" BETWEEN 10 AND 300");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Policies_RemoteIdleTimeoutMinutes",
                table: "Policies",
                sql: "\"RemoteIdleTimeoutMinutes\" BETWEEN 5 AND 480");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Policies_RemoteMaxFileBytes",
                table: "Policies",
                sql: "\"RemoteMaxFileBytes\" BETWEEN 1048576 AND 10737418240");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionActions_ParticipantId",
                table: "RemoteSessionActions",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionActions_SessionId_ClientId",
                table: "RemoteSessionActions",
                columns: new[] { "SessionId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionActions_SessionId_Time",
                table: "RemoteSessionActions",
                columns: new[] { "SessionId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionActions_Time",
                table: "RemoteSessionActions",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionParticipants_Active",
                table: "RemoteSessionParticipants",
                columns: new[] { "State", "CreatedAt" },
                filter: "\"State\" IN ('Requested', 'Signed', 'Connecting', 'Connected')");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionParticipants_EndpointId_ClientId",
                table: "RemoteSessionParticipants",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionParticipants_SessionId",
                table: "RemoteSessionParticipants",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessionParticipants_SessionId_ClientId",
                table: "RemoteSessionParticipants",
                columns: new[] { "SessionId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_CreatedAt",
                table: "RemoteSessions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_EndpointId_ClientId",
                table: "RemoteSessions",
                columns: new[] { "EndpointId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_EndpointId_CreatedAt",
                table: "RemoteSessions",
                columns: new[] { "EndpointId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_Open",
                table: "RemoteSessions",
                column: "Id",
                filter: "\"EndedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RestoreSigningRequestOrigin.FunctionSql);

            migrationBuilder.DropTable(
                name: "RemoteSessionActions");

            migrationBuilder.DropTable(
                name: "RemoteSessionParticipants");

            migrationBuilder.DropTable(
                name: "RemoteSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Policies_RemoteConsentTimeoutSeconds",
                table: "Policies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Policies_RemoteIdleTimeoutMinutes",
                table: "Policies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Policies_RemoteMaxFileBytes",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteBannerVisible",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteClipboardEnabled",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteConsentRequired",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteConsentTimeoutSeconds",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteIdleTimeoutMinutes",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "RemoteMaxFileBytes",
                table: "Policies");
        }
    }
}
