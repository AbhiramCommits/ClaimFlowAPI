-- 010_views.sql
-- Reporting views. Each view is separated by GO because CREATE VIEW must be the
-- first statement in its batch.
SET NOCOUNT ON;
GO

CREATE OR ALTER VIEW dbo.vw_ClaimFinancials
AS
WITH Base AS
(
    SELECT
        c.Id,
        c.ClaimId,
        c.PayerId,
        COALESCE(p.Name, c.PayerId) AS PayerName,
        c.MemberId,
        c.ServiceDate,
        c.Status,
        c.Outcome,
        c.DenialReasonCode,
        c.TotalChargeCents,
        c.AllowedCents,
        c.PaidCents,
        c.PatientResponsibilityCents,
        c.ReceivedAt,
        c.AdjudicatedAt,
        DATEDIFF(day, c.ServiceDate,
            COALESCE(CAST(c.AdjudicatedAt AS date), CAST(GETUTCDATE() AS date))) AS DaysInAr
    FROM dbo.Claims AS c
    LEFT JOIN dbo.Payers AS p ON p.PayerId = c.PayerId
    WHERE c.Status <> 'Rejected'
)
SELECT
    Id,
    ClaimId,
    PayerId,
    PayerName,
    MemberId,
    ServiceDate,
    Status,
    Outcome,
    DenialReasonCode,
    CASE DenialReasonCode
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
        ELSE NULL
    END AS DenialReasonDescription,
    TotalChargeCents,
    AllowedCents,
    PaidCents,
    PatientResponsibilityCents,
    ReceivedAt,
    AdjudicatedAt,
    DaysInAr,
    CASE
        WHEN DaysInAr <= 30 THEN '0-30'
        WHEN DaysInAr <= 60 THEN '31-60'
        WHEN DaysInAr <= 90 THEN '61-90'
        ELSE '90+'
    END AS AgingBucket
FROM Base;
GO

CREATE OR ALTER VIEW dbo.vw_ClaimDenials
AS
SELECT
    ClaimId,
    PayerId,
    PayerName,
    MemberId,
    ServiceDate,
    Status,
    Outcome,
    DenialReasonCode,
    DenialReasonDescription,
    TotalChargeCents,
    AllowedCents,
    PaidCents,
    PatientResponsibilityCents,
    AdjudicatedAt
FROM dbo.vw_ClaimFinancials
WHERE DenialReasonCode IS NOT NULL
  AND Status IN ('Paid', 'Denied');
GO
