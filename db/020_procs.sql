-- 020_procs.sql
-- Reporting stored procedures. Every proc:
--   * SET NOCOUNT ON
--   * copies parameters into local variables to blunt parameter sniffing
--   * ends with OPTION (RECOMPILE) for the reporting workload
--   * documents the 030 index it relies on
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.usp_DenialReasonRollup
    @FromDate date = NULL,
    @ToDate date = NULL,
    @PayerId nvarchar(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @From date = ISNULL(@FromDate, DATEADD(day, -30, CAST(GETUTCDATE() AS date)));
    DECLARE @To date = ISNULL(@ToDate, CAST(GETUTCDATE() AS date));
    DECLARE @Payer nvarchar(32) = NULLIF(LTRIM(RTRIM(@PayerId)), '');

    -- Index: IX_Claims_Reporting_Denials (DenialReasonCode, ServiceDate)
    --        or IX_Claims_Reporting_Denials_Payer (PayerId, ServiceDate).
    ;WITH Denials AS
    (
        SELECT
            c.DenialReasonCode AS DenialCode,
            CASE c.DenialReasonCode
                WHEN 'CO11' THEN 'Diagnosis inconsistent with the procedure'
                WHEN 'CO16' THEN 'Claim/service lacks information or has submission/billing error'
                WHEN 'CO18' THEN 'Duplicate claim/service'
                WHEN 'CO27' THEN 'Expenses incurred after coverage terminated'
                WHEN 'CO29' THEN 'The time limit for filing has expired'
                WHEN 'CO50' THEN 'Non-covered because not deemed a medical necessity'
                WHEN 'CO97' THEN 'Benefit included in another service (bundled)'
                WHEN 'CO109' THEN 'Claim/service not covered by this payer/contractor'
                WHEN 'CO151' THEN 'Information does not support this many services'
                WHEN 'PR1' THEN 'Deductible amount'
                WHEN 'PR204' THEN 'Service not covered under the current benefit plan'
                ELSE c.DenialReasonCode
            END AS Description,
            c.TotalChargeCents
        FROM dbo.Claims AS c
        WHERE c.Status <> 'Rejected'
          AND c.DenialReasonCode IS NOT NULL
          AND c.ServiceDate >= @From
          AND c.ServiceDate <= @To
          AND (@Payer IS NULL OR c.PayerId = @Payer)
    ),
    Rollup AS
    (
        SELECT
            DenialCode,
            Description,
            COUNT_BIG(*) AS ClaimCount,
            SUM(CAST(TotalChargeCents AS bigint)) AS ChargeCents
        FROM Denials
        GROUP BY DenialCode, Description
    )
    SELECT
        DenialCode,
        Description,
        CONVERT(int, ClaimCount) AS ClaimCount,
        ChargeCents,
        CAST(100.0 * ClaimCount / NULLIF(SUM(ClaimCount) OVER (), 0) AS decimal(9, 2)) AS PercentOfDenials,
        CONVERT(int, RANK() OVER (ORDER BY ClaimCount DESC, DenialCode)) AS Rank
    FROM Rollup
    ORDER BY ClaimCount DESC, DenialCode
    OPTION (RECOMPILE);
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_DaysInAR
    @AsOfDate date = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AsOf date = ISNULL(@AsOfDate, CAST(GETUTCDATE() AS date));

    -- Index: IX_Claims_Reporting_Ar (Status, ServiceDate) INCLUDE the money columns.
    -- Open A/R = unadjudicated claims plus patient-responsibility balances on paid claims.
    ;WITH OpenClaims AS
    (
        SELECT
            c.PayerId,
            COALESCE(p.Name, c.PayerId) AS PayerName,
            CASE
                WHEN c.Status IN ('Validated', 'Adjudicated') THEN CAST(c.TotalChargeCents AS bigint)
                WHEN c.Status = 'Paid' AND c.PatientResponsibilityCents > 0 THEN CAST(c.PatientResponsibilityCents AS bigint)
                ELSE 0
            END AS OpenCents,
            DATEDIFF(day, c.ServiceDate, @AsOf) AS DaysInAr
        FROM dbo.Claims AS c
        LEFT JOIN dbo.Payers AS p ON p.PayerId = c.PayerId
        WHERE c.Status <> 'Rejected'
          AND (c.Status IN ('Validated', 'Adjudicated')
               OR (c.Status = 'Paid' AND c.PatientResponsibilityCents > 0))
    ),
    WithMedian AS
    (
        SELECT
            *,
            PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY DaysInAr)
                OVER (PARTITION BY PayerId) AS MedianDaysInAr
        FROM OpenClaims
    )
    SELECT
        PayerId,
        PayerName,
        COUNT_BIG(*) AS OpenClaimCount,
        SUM(OpenCents) AS OpenArCents,
        CAST(AVG(CAST(DaysInAr AS decimal(10, 2))) AS decimal(10, 2)) AS AverageDaysInAr,
        CAST(MAX(MedianDaysInAr) AS decimal(10, 2)) AS MedianDaysInAr,
        SUM(CASE WHEN DaysInAr <= 30 THEN 1 ELSE 0 END) AS Bucket0To30Claims,
        SUM(CASE WHEN DaysInAr <= 30 THEN OpenCents ELSE 0 END) AS Bucket0To30Cents,
        SUM(CASE WHEN DaysInAr > 30 AND DaysInAr <= 60 THEN 1 ELSE 0 END) AS Bucket31To60Claims,
        SUM(CASE WHEN DaysInAr > 30 AND DaysInAr <= 60 THEN OpenCents ELSE 0 END) AS Bucket31To60Cents,
        SUM(CASE WHEN DaysInAr > 60 AND DaysInAr <= 90 THEN 1 ELSE 0 END) AS Bucket61To90Claims,
        SUM(CASE WHEN DaysInAr > 60 AND DaysInAr <= 90 THEN OpenCents ELSE 0 END) AS Bucket61To90Cents,
        SUM(CASE WHEN DaysInAr > 90 THEN 1 ELSE 0 END) AS Bucket90PlusClaims,
        SUM(CASE WHEN DaysInAr > 90 THEN OpenCents ELSE 0 END) AS Bucket90PlusCents
    FROM WithMedian
    GROUP BY PayerId, PayerName
    ORDER BY OpenArCents DESC, PayerId
    OPTION (RECOMPILE);
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ValidationRejectSummary
    @FromDate date = NULL,
    @ToDate date = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @From date = ISNULL(@FromDate, DATEADD(day, -30, CAST(GETUTCDATE() AS date)));
    DECLARE @To date = ISNULL(@ToDate, CAST(GETUTCDATE() AS date));

    -- Index: IX_Claims_Reporting_Rejects (Status, ReceivedAt) INCLUDE (ValidationRuleCodes).
    DECLARE @Submissions bigint =
    (
        SELECT COUNT_BIG(*)
        FROM dbo.Claims
        WHERE CAST(ReceivedAt AS date) >= @From
          AND CAST(ReceivedAt AS date) <= @To
    );

    ;WITH Rejected AS
    (
        SELECT LTRIM(RTRIM(s.value)) AS RuleCode
        FROM dbo.Claims AS c
        CROSS APPLY STRING_SPLIT(c.ValidationRuleCodes, ',') AS s
        WHERE c.Status = 'Rejected'
          AND c.ValidationRuleCodes <> ''
          AND CAST(c.ReceivedAt AS date) >= @From
          AND CAST(c.ReceivedAt AS date) <= @To
    ),
    Rollup AS
    (
        SELECT RuleCode, COUNT_BIG(*) AS RejectCount
        FROM Rejected
        GROUP BY RuleCode
    )
    SELECT
        RuleCode,
        CASE RuleCode
            WHEN 'MBR001' THEN 'Member identifier is missing'
            WHEN 'PAY001' THEN 'Payer is not in the contracted payer directory'
            WHEN 'SVC001' THEN 'Service date is in the future'
            WHEN 'SVC002' THEN 'Service date is older than the payer filing limit'
            WHEN 'SVC003' THEN 'Service date is missing'
            WHEN 'NPI001' THEN 'Provider NPI failed format or Luhn checksum validation'
            WHEN 'ICD001' THEN 'Diagnosis code is not a valid ICD-10-CM code'
            WHEN 'CPT001' THEN 'Procedure code is not a valid CPT/HCPCS code'
            WHEN 'CHG001' THEN 'Line or claim charge is zero or negative'
            WHEN 'CHG002' THEN 'Line charges do not sum to the claim total'
            WHEN 'DUP001' THEN 'Duplicate service line on the same claim'
            WHEN 'POS001' THEN 'Place of service is missing or invalid'
            ELSE RuleCode
        END AS Description,
        CONVERT(int, RejectCount) AS RejectCount,
        CAST(100.0 * RejectCount / NULLIF(@Submissions, 0) AS decimal(9, 2)) AS PercentOfSubmissions
    FROM Rollup
    ORDER BY RejectCount DESC, RuleCode
    OPTION (RECOMPILE);
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ClaimThroughputByHour
    @FromDate date = NULL,
    @ToDate date = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @From date = ISNULL(@FromDate, DATEADD(day, -30, CAST(GETUTCDATE() AS date)));
    DECLARE @To date = ISNULL(@ToDate, CAST(GETUTCDATE() AS date));

    -- Index: IX_Claims_Reporting_Throughput (ReceivedAt) INCLUDE (AdjudicatedAt, Status).
    ;WITH Ingest AS
    (
        SELECT
            DATEADD(hour, DATEDIFF(hour, 0, CAST(c.ReceivedAt AS datetime2)), 0) AS HourStart,
            COUNT_BIG(*) AS IngestCount
        FROM dbo.Claims AS c
        WHERE CAST(c.ReceivedAt AS date) >= @From
          AND CAST(c.ReceivedAt AS date) <= @To
        GROUP BY DATEADD(hour, DATEDIFF(hour, 0, CAST(c.ReceivedAt AS datetime2)), 0)
    ),
    Adjudicated AS
    (
        SELECT
            DATEADD(hour, DATEDIFF(hour, 0, CAST(c.AdjudicatedAt AS datetime2)), 0) AS HourStart,
            COUNT_BIG(*) AS AdjudicationCount
        FROM dbo.Claims AS c
        WHERE c.AdjudicatedAt IS NOT NULL
          AND CAST(c.AdjudicatedAt AS date) >= @From
          AND CAST(c.AdjudicatedAt AS date) <= @To
        GROUP BY DATEADD(hour, DATEDIFF(hour, 0, CAST(c.AdjudicatedAt AS datetime2)), 0)
    ),
    Hours AS
    (
        SELECT HourStart FROM Ingest
        UNION
        SELECT HourStart FROM Adjudicated
    )
    SELECT
        CONVERT(datetime2, h.HourStart) AS HourStartUtc,
        CONVERT(int, ISNULL(i.IngestCount, 0)) AS IngestedCount,
        CONVERT(int, ISNULL(a.AdjudicationCount, 0)) AS AdjudicatedCount
    FROM Hours AS h
    LEFT JOIN Ingest AS i ON i.HourStart = h.HourStart
    LEFT JOIN Adjudicated AS a ON a.HourStart = h.HourStart
    ORDER BY h.HourStart
    OPTION (RECOMPILE);
END;
GO
