-- FC-SUP-01 diagnostic: GG-003 candidate CNC counter increments by Gajra production shift.
-- READ ONLY. Run on an explicitly selected local restored development database.
-- Do not execute against live production or use this as accepted production/OEE evidence.
-- SQL Server; Gajra IST fixed UTC+05:30 for the sampled 2026 interval.
-- Observed timestamp is NOT necessarily the physical completion timestamp.
-- An increment spanning shift boundaries is counted at observation time but flagged ambiguous.
-- A missing row is NO EVIDENCE, never a zero-production claim.
SET NOCOUNT ON;
DECLARE @MachineId uniqueidentifier = '677B030B-1EDE-4A67-82AE-6A494791357F';
DECLARE @StartProductionDate date = '2026-10-28';
DECLARE @EndProductionDate date = '2026-10-31';
;WITH NumericReadings AS (
    SELECT Position, ObservedAt, InstanceId,
           TRY_CONVERT(bigint, ObservationValue) AS CounterValue
    FROM dbo.MachineObservation
    WHERE MachineId = @MachineId
      AND Address = 'DEWAS-SR-GG-003_p1_part_count_complete'
      AND Quality = 0
), Ordered AS (
    SELECT *,
           LAG(CounterValue) OVER (ORDER BY Position) AS PreviousValue,
           LAG(ObservedAt) OVER (ORDER BY Position) AS PreviousObservedAt,
           LAG(InstanceId) OVER (ORDER BY Position) AS PreviousInstanceId
    FROM NumericReadings WHERE CounterValue IS NOT NULL
), Localized AS (
    SELECT *, DATEADD(MINUTE,330,CAST(ObservedAt AS datetime2(7))) AS ObservedIST,
           DATEADD(MINUTE,330,CAST(PreviousObservedAt AS datetime2(7))) AS PreviousIST
    FROM Ordered
), Shifted AS (
    SELECT *,
      CASE WHEN CONVERT(time,ObservedIST) < '07:30'
           THEN DATEADD(DAY,-1,CONVERT(date,ObservedIST))
           ELSE CONVERT(date,ObservedIST) END AS ProductionDate,
      CASE WHEN CONVERT(time,ObservedIST) >= '07:30' AND CONVERT(time,ObservedIST) < '16:00' THEN 1
           WHEN CONVERT(time,ObservedIST) >= '16:00' THEN 2 ELSE 3 END AS ShiftNumber,
      CASE WHEN PreviousIST IS NULL THEN NULL
           WHEN CONVERT(time,PreviousIST) < '07:30'
           THEN DATEADD(DAY,-1,CONVERT(date,PreviousIST))
           ELSE CONVERT(date,PreviousIST) END AS PreviousProductionDate,
      CASE WHEN PreviousIST IS NULL THEN NULL
           WHEN CONVERT(time,PreviousIST) >= '07:30' AND CONVERT(time,PreviousIST) < '16:00' THEN 1
           WHEN CONVERT(time,PreviousIST) >= '16:00' THEN 2 ELSE 3 END AS PreviousShiftNumber
    FROM Localized
), Classified AS (
    SELECT *,
      CASE WHEN PreviousValue IS NOT NULL AND CounterValue - PreviousValue = 1 THEN 1 ELSE 0 END AS UnitIncrement,
      CASE WHEN PreviousValue IS NOT NULL AND CounterValue - PreviousValue = 1
             AND (PreviousProductionDate <> ProductionDate OR PreviousShiftNumber <> ShiftNumber)
           THEN 1 ELSE 0 END AS BoundaryAmbiguousIncrement,
      CASE WHEN PreviousValue IS NOT NULL AND CounterValue - PreviousValue = 1
             AND (DATEDIFF_BIG(SECOND,PreviousObservedAt,ObservedAt) > 1800
                  OR PreviousInstanceId <> InstanceId)
           THEN 1 ELSE 0 END AS GapFlaggedIncrement,
      CASE WHEN PreviousValue IS NOT NULL AND CounterValue - PreviousValue <> 0
             AND CounterValue - PreviousValue <> 1 THEN 1 ELSE 0 END AS NonUnitTransition
    FROM Shifted
)
SELECT ProductionDate, ShiftNumber,
       COUNT_BIG(*) AS NumericReadings,
       SUM(UnitIncrement) AS ProvisionalObservedIncrements,
       SUM(BoundaryAmbiguousIncrement) AS BoundaryAmbiguousIncrements,
       SUM(GapFlaggedIncrement) AS GapFlaggedIncrements,
       SUM(NonUnitTransition) AS NonUnitTransitions,
       MIN(ObservedIST) AS FirstObservedIST,
       MAX(ObservedIST) AS LastObservedIST
FROM Classified
WHERE ProductionDate BETWEEN @StartProductionDate AND @EndProductionDate
GROUP BY ProductionDate, ShiftNumber
ORDER BY ProductionDate, ShiftNumber;
