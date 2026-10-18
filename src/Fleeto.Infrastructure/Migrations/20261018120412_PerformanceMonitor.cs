using System;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fleeto.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceMonitor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PerfDatabaseSnapshots",
                columns: table => new
                {
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BlocksHit = table.Column<long>(type: "bigint", nullable: false),
                    BlocksRead = table.Column<long>(type: "bigint", nullable: false),
                    TempFiles = table.Column<long>(type: "bigint", nullable: false),
                    TempBytes = table.Column<long>(type: "bigint", nullable: false),
                    Deadlocks = table.Column<long>(type: "bigint", nullable: false),
                    Commits = table.Column<long>(type: "bigint", nullable: false),
                    Rollbacks = table.Column<long>(type: "bigint", nullable: false),
                    Connections = table.Column<int>(type: "integer", nullable: false),
                    MaxConnections = table.Column<int>(type: "integer", nullable: false),
                    WraparoundAge = table.Column<long>(type: "bigint", nullable: false),
                    DatabaseBytes = table.Column<long>(type: "bigint", nullable: false),
                    StatisticsResetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatementsProblem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfDatabaseSnapshots", x => x.Time);
                });

            migrationBuilder.CreateTable(
                name: "PerfEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    DbCount = table.Column<int>(type: "integer", nullable: false),
                    DbMs = table.Column<double>(type: "double precision", nullable: false),
                    ExternalMs = table.Column<double>(type: "double precision", nullable: false),
                    Failed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PerfHostMinutes",
                columns: table => new
                {
                    Minute = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Cores = table.Column<int>(type: "integer", nullable: true),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: true),
                    StealPercent = table.Column<double>(type: "double precision", nullable: true),
                    IowaitPercent = table.Column<double>(type: "double precision", nullable: true),
                    Load1 = table.Column<double>(type: "double precision", nullable: true),
                    MemoryTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    MemoryAvailableBytes = table.Column<long>(type: "bigint", nullable: true),
                    SwapTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    SwapFreeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DiskLatencyMs = table.Column<double>(type: "double precision", nullable: true),
                    DiskUtilizationPercent = table.Column<double>(type: "double precision", nullable: true),
                    DiskTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    DiskFreeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ThrottledPercent = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfHostMinutes", x => x.Minute);
                });

            migrationBuilder.CreateTable(
                name: "PerfHours",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Hour = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Errors = table.Column<long>(type: "bigint", nullable: false),
                    Sum = table.Column<double>(type: "double precision", nullable: false),
                    Min = table.Column<double>(type: "double precision", nullable: false),
                    Max = table.Column<double>(type: "double precision", nullable: false),
                    Histogram = table.Column<int[]>(type: "integer[]", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    DbCount = table.Column<long>(type: "bigint", nullable: false),
                    DbMs = table.Column<double>(type: "double precision", nullable: false),
                    ExternalMs = table.Column<double>(type: "double precision", nullable: false),
                    PeakCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfHours", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PerfMarkers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfMarkers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PerfMinutes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Minute = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Errors = table.Column<int>(type: "integer", nullable: false),
                    Sum = table.Column<double>(type: "double precision", nullable: false),
                    Min = table.Column<double>(type: "double precision", nullable: false),
                    Max = table.Column<double>(type: "double precision", nullable: false),
                    Histogram = table.Column<int[]>(type: "integer[]", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    DbCount = table.Column<int>(type: "integer", nullable: false),
                    DbMs = table.Column<double>(type: "double precision", nullable: false),
                    ExternalMs = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfMinutes", x => new { x.Minute, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "PerformanceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformanceSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PerfRuntimeMinutes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Minute = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: false),
                    CpuPercentMax = table.Column<double>(type: "double precision", nullable: false),
                    Cores = table.Column<int>(type: "integer", nullable: false),
                    WorkingSetBytes = table.Column<long>(type: "bigint", nullable: false),
                    HeapBytes = table.Column<long>(type: "bigint", nullable: false),
                    GcPauseMs = table.Column<double>(type: "double precision", nullable: false),
                    AllocatedBytes = table.Column<long>(type: "bigint", nullable: false),
                    ThreadPoolThreads = table.Column<int>(type: "integer", nullable: false),
                    ThreadPoolQueue = table.Column<int>(type: "integer", nullable: false),
                    LockContentions = table.Column<long>(type: "bigint", nullable: false),
                    Exceptions = table.Column<int>(type: "integer", nullable: false),
                    Connections = table.Column<int>(type: "integer", nullable: false),
                    DbPoolBusy = table.Column<int>(type: "integer", nullable: false),
                    DbPoolIdle = table.Column<int>(type: "integer", nullable: false),
                    DbPoolMax = table.Column<int>(type: "integer", nullable: false),
                    DbPoolTimeouts = table.Column<int>(type: "integer", nullable: false),
                    OverheadMs = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfRuntimeMinutes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PerfStatementSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QueryId = table.Column<long>(type: "bigint", nullable: false),
                    Query = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Calls = table.Column<long>(type: "bigint", nullable: false),
                    TotalMs = table.Column<double>(type: "double precision", nullable: false),
                    Rows = table.Column<long>(type: "bigint", nullable: false),
                    SharedBlocksHit = table.Column<long>(type: "bigint", nullable: false),
                    SharedBlocksRead = table.Column<long>(type: "bigint", nullable: false),
                    TempBlocksWritten = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfStatementSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PerfTableSnapshots",
                columns: table => new
                {
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Table = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Rows = table.Column<long>(type: "bigint", nullable: false),
                    SequentialScans = table.Column<long>(type: "bigint", nullable: false),
                    SequentialRowsRead = table.Column<long>(type: "bigint", nullable: false),
                    IndexScans = table.Column<long>(type: "bigint", nullable: false),
                    LiveRows = table.Column<long>(type: "bigint", nullable: false),
                    DeadRows = table.Column<long>(type: "bigint", nullable: false),
                    LastVacuum = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAnalyze = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsHypertable = table.Column<bool>(type: "boolean", nullable: false),
                    Compressed = table.Column<bool>(type: "boolean", nullable: false),
                    RetentionPolicy = table.Column<bool>(type: "boolean", nullable: false),
                    Chunks = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfTableSnapshots", x => new { x.Time, x.Table });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PerfEvents_Time",
                table: "PerfEvents",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_PerfHours_Hour_Kind",
                table: "PerfHours",
                columns: new[] { "Hour", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_PerfMarkers_Time",
                table: "PerfMarkers",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_PerfMinutes_Kind_Minute",
                table: "PerfMinutes",
                columns: new[] { "Kind", "Minute" });

            migrationBuilder.CreateIndex(
                name: "IX_PerfRuntimeMinutes_Minute_Component",
                table: "PerfRuntimeMinutes",
                columns: new[] { "Minute", "Component" });

            migrationBuilder.CreateIndex(
                name: "IX_PerfStatementSnapshots_Time_QueryId",
                table: "PerfStatementSnapshots",
                columns: new[] { "Time", "QueryId" });

            migrationBuilder.Sql(PerformanceSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PerformanceSql.Down);

            migrationBuilder.DropTable(
                name: "PerfDatabaseSnapshots");

            migrationBuilder.DropTable(
                name: "PerfEvents");

            migrationBuilder.DropTable(
                name: "PerfHostMinutes");

            migrationBuilder.DropTable(
                name: "PerfHours");

            migrationBuilder.DropTable(
                name: "PerfMarkers");

            migrationBuilder.DropTable(
                name: "PerfMinutes");

            migrationBuilder.DropTable(
                name: "PerformanceSettings");

            migrationBuilder.DropTable(
                name: "PerfRuntimeMinutes");

            migrationBuilder.DropTable(
                name: "PerfStatementSnapshots");

            migrationBuilder.DropTable(
                name: "PerfTableSnapshots");
        }
    }
}
