using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ClaimFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Patients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MemberId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GivenName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FamilyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                    SsnLast4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    SsnHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payers",
                columns: table => new
                {
                    PayerId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FilingLimitDays = table.Column<int>(type: "int", nullable: false),
                    FeeSchedulePercent = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payers", x => x.PayerId);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Consumer = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayerId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MemberId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderNpi = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PlaceOfService = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalChargeCents = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    DenialReasonCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    AllowedCents = table.Column<int>(type: "int", nullable: false),
                    PaidCents = table.Column<int>(type: "int", nullable: false),
                    PatientResponsibilityCents = table.Column<int>(type: "int", nullable: false),
                    ValidationRuleCodes = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAtUnixMs = table.Column<long>(type: "bigint", nullable: false),
                    LastStatusChangeAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AdjudicatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Claims_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ClaimLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    CptCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Icd10Code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PlaceOfService = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    ChargeCents = table.Column<int>(type: "int", nullable: false),
                    Units = table.Column<int>(type: "int", nullable: false),
                    AllowedCents = table.Column<int>(type: "int", nullable: false),
                    PaidCents = table.Column<int>(type: "int", nullable: false),
                    PatientResponsibilityCents = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimLines_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClaimStatusHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimStatusHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimStatusHistory_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Payers",
                columns: new[] { "PayerId", "FeeSchedulePercent", "FilingLimitDays", "Name" },
                values: new object[,]
                {
                    { "PAYER-AETNA", 72, 180, "Aetna" },
                    { "PAYER-BCBS", 68, 365, "Blue Cross Blue Shield" },
                    { "PAYER-CIGNA", 70, 180, "Cigna" },
                    { "PAYER-HUMANA", 74, 365, "Humana" },
                    { "PAYER-MEDICARE", 80, 365, "Medicare" },
                    { "PAYER-UHC", 71, 270, "UnitedHealthcare" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimLines_ClaimId",
                table: "ClaimLines",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimLines_CptCode",
                table: "ClaimLines",
                column: "CptCode");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_ClaimId",
                table: "Claims",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_MemberId",
                table: "Claims",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_PatientId",
                table: "Claims",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_Payer_Denial",
                table: "Claims",
                columns: new[] { "PayerId", "DenialReasonCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_Payer_ServiceDate",
                table: "Claims",
                columns: new[] { "PayerId", "ServiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_Received_Status",
                table: "Claims",
                columns: new[] { "ReceivedAtUnixMs", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_ServiceDate_Status",
                table: "Claims",
                columns: new[] { "ServiceDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_Status",
                table: "Claims",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_Claims_NaturalKey",
                table: "Claims",
                columns: new[] { "PayerId", "MemberId", "ServiceDate", "TotalChargeCents" },
                unique: true,
                filter: "[Status] <> 'Rejected'");

            migrationBuilder.CreateIndex(
                name: "IX_StatusHistory_Claim_Sequence",
                table: "ClaimStatusHistory",
                columns: new[] { "ClaimId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Pending",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Patients_MemberId",
                table: "Patients",
                column: "MemberId",
                unique: true,
                filter: "[MemberId] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedEvents_ClaimId",
                table: "ProcessedEvents",
                column: "ClaimId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClaimLines");

            migrationBuilder.DropTable(
                name: "ClaimStatusHistory");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "Payers");

            migrationBuilder.DropTable(
                name: "ProcessedEvents");

            migrationBuilder.DropTable(
                name: "Claims");

            migrationBuilder.DropTable(
                name: "Patients");
        }
    }
}
