-- 030_indexes.sql
-- Covering indexes used by the denial, A/R, rejection and throughput rollups.
-- Idempotent: safe to run on every migration.
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Claims_Reporting_Denials' AND object_id = OBJECT_ID('dbo.Claims'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Claims_Reporting_Denials
        ON dbo.Claims (DenialReasonCode, ServiceDate)
        INCLUDE (PayerId, TotalChargeCents, Status)
        WHERE DenialReasonCode IS NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Claims_Reporting_Denials_Payer' AND object_id = OBJECT_ID('dbo.Claims'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Claims_Reporting_Denials_Payer
        ON dbo.Claims (PayerId, ServiceDate)
        INCLUDE (DenialReasonCode, TotalChargeCents, Status)
        WHERE DenialReasonCode IS NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Claims_Reporting_Ar' AND object_id = OBJECT_ID('dbo.Claims'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Claims_Reporting_Ar
        ON dbo.Claims (Status, ServiceDate)
        INCLUDE (PayerId, TotalChargeCents, PaidCents, PatientResponsibilityCents);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Claims_Reporting_Rejects' AND object_id = OBJECT_ID('dbo.Claims'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Claims_Reporting_Rejects
        ON dbo.Claims (Status, ReceivedAt)
        INCLUDE (ValidationRuleCodes);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Claims_Reporting_Throughput' AND object_id = OBJECT_ID('dbo.Claims'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Claims_Reporting_Throughput
        ON dbo.Claims (ReceivedAt)
        INCLUDE (AdjudicatedAt, Status);
END;
GO
