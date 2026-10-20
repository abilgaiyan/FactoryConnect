-- REVIEW CANDIDATE ONLY. Q2 execution requires separate exact SQL/limits approval.
-- Completed Q0/Q1 are not repeated; these guards only bind this packet to its target.
DECLARE @ApprovedServer sysname = N'DESKTOP-1PJK2OB\FCINVENTORY';
DECLARE @ApprovedDatabase sysname = N'FactoryConnect_GG003_IMP1C_INVENTORY_20261020';
DECLARE @MachineId uniqueidentifier = '677b030b-1ede-4a67-82ae-6a494791357f';
IF CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) COLLATE Latin1_General_100_BIN2
    <> @ApprovedServer COLLATE Latin1_General_100_BIN2
 OR DB_NAME() COLLATE Latin1_General_100_BIN2 <> @ApprovedDatabase COLLATE Latin1_General_100_BIN2
    THROW 51100, 'Q2 approved disposable target mismatch.', 1;
IF NOT EXISTS(SELECT 1 FROM sys.databases WHERE database_id=DB_ID()
              AND state_desc=N'ONLINE' AND is_read_only=1)
    THROW 51101, 'Q2 requires the unchanged ONLINE READ_ONLY inventory copy.', 1;

IF ISNULL(IS_SRVROLEMEMBER('sysadmin'),1)<>0 OR ISNULL(IS_ROLEMEMBER('db_owner'),1)<>0
 THROW 51104, 'Restricted-role check failed or indeterminate.', 1;

-- Populate ONLY after Q2A review; never automatically select MAX(Position).
DECLARE @ProcessorRowId bigint=NULL, @StreamRowId bigint=NULL;
DECLARE @CutText varchar(max)=NULL, @AfterPositionText varchar(max)='0', @PageSize int=200;
IF @ProcessorRowId IS NULL OR @StreamRowId IS NULL OR @CutText IS NULL
 THROW 51110, 'Approve exact processor/stream/cut before use.', 1;
IF @AfterPositionText IS NULL OR DATALENGTH(@CutText) NOT BETWEEN 1 AND 20
 OR DATALENGTH(@AfterPositionText) NOT BETWEEN 1 AND 20
 OR @CutText COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9]%'
 OR @AfterPositionText COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9]%'
 OR (DATALENGTH(@CutText)>1 AND LEFT(@CutText,1)='0')
 OR (DATALENGTH(@AfterPositionText)>1 AND LEFT(@AfterPositionText,1)='0')
 THROW 51111, 'Require canonical unsigned decimal text, at most 20 digits.', 1;
IF (DATALENGTH(@CutText)=20 AND @CutText COLLATE Latin1_General_100_BIN2>'18446744073709551615')
 OR (DATALENGTH(@AfterPositionText)=20 AND @AfterPositionText COLLATE Latin1_General_100_BIN2>'18446744073709551615')
 THROW 51111, 'Position exceeds UInt64 domain.', 1;
DECLARE @Cut decimal(20,0)=TRY_CONVERT(decimal(20,0),@CutText);
DECLARE @AfterPosition decimal(20,0)=TRY_CONVERT(decimal(20,0),@AfterPositionText);
IF @Cut IS NULL OR @Cut<1 OR @AfterPosition IS NULL OR @AfterPosition>@Cut
 OR @ProcessorRowId<1 OR @StreamRowId<1 OR @PageSize<1 OR @PageSize>200
 THROW 51111, 'Invalid reviewed cut/page bounds.', 1;
IF NOT EXISTS(SELECT 1 FROM dbo.MetricAggregationProcessor p
 JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=p.MetricInputStreamRowId
 JOIN dbo.MetricAggregationRevision r ON r.MetricAggregationProcessorRowId=p.MetricAggregationProcessorRowId
 WHERE p.MetricAggregationProcessorRowId=@ProcessorRowId
   AND s.MetricInputStreamRowId=@StreamRowId AND s.MachineId=@MachineId AND r.Position=@Cut)
    THROW 51112, 'Exact machine/processor/stream/revision binding absent.', 1;

SELECT N'Q2B-cut' AS ResultSet,@MachineId AS MachineId,@ProcessorRowId AS ProcessorRowId,
       @StreamRowId AS MetricInputStreamRowId,@Cut AS ExactAggregationRevision,
       @AfterPosition AS AfterPosition,@PageSize AS PageSize,SYSDATETIMEOFFSET() AS CapturedAt;
-- ALL incorporated contributions through this cut, across all business dates.
SELECT N'Q2B-membership-summary' AS ResultSet,COUNT_BIG(*) AS ContributionCount,
       MIN(c.Position) AS FirstPosition,MAX(c.Position) AS LastPosition,
       COUNT_BIG(CASE WHEN f.MetricInputFactRowId IS NULL THEN 1 END) AS MissingFactJoinCount,
       COUNT_BIG(CASE WHEN c.MetricInputStreamRowId<>@StreamRowId THEN 1 END) AS WrongContributionStreamCount,
       COUNT_BIG(CASE WHEN rawFact.MetricInputFactRowId IS NULL THEN 1 END) AS ReferencedFactAbsentCount,
       COUNT_BIG(CASE WHEN rawFact.MetricInputFactRowId IS NOT NULL AND
         (rawFact.MetricInputStreamRowId<>c.MetricInputStreamRowId OR rawFact.Position<>c.Position)
         THEN 1 END) AS FactBindingMismatchCount,
       COUNT_BIG(CASE WHEN rawFact.MetricInputFactRowId IS NOT NULL AND rawFact.MachineId<>@MachineId
         THEN 1 END) AS WrongExistingFactMachineCount,
       COUNT_BIG(CASE WHEN rawFact.MetricInputFactRowId IS NULL THEN 1 END) AS FactMachineUnverifiableCount
FROM dbo.MetricAggregationContribution c
LEFT JOIN dbo.MetricInputFact rawFact ON rawFact.MetricInputFactRowId=c.MetricInputFactRowId
LEFT JOIN dbo.MetricInputFact f ON f.MetricInputFactRowId=c.MetricInputFactRowId
 AND f.MetricInputStreamRowId=c.MetricInputStreamRowId AND f.Position=c.Position
WHERE c.MetricAggregationProcessorRowId=@ProcessorRowId AND c.Position<=@Cut;

SELECT N'Q2B-period-key-counts' AS ResultSet,c.MetricInputStreamRowId,
       f.ProductionDaySiteId,f.ProductionBusinessDate,f.MetricInputKey,f.Unit,
       f.IsPlannedProductionTime,COUNT_BIG(*) AS ContributionCount,
       MIN(c.Position) AS FirstPosition,MAX(c.Position) AS LastPosition,
       COUNT_BIG(CASE WHEN f.SourceEligibilityIntervalId IS NULL THEN 1 END) AS NullEligibilityReferenceCount,
       COUNT_BIG(CASE WHEN f.SourceContextualizedActivityIntervalId IS NULL THEN 1 END) AS NullContextReferenceCount
FROM dbo.MetricAggregationContribution c LEFT JOIN dbo.MetricInputFact f
 ON f.MetricInputFactRowId=c.MetricInputFactRowId
 AND f.MetricInputStreamRowId=c.MetricInputStreamRowId AND f.Position=c.Position
WHERE c.MetricAggregationProcessorRowId=@ProcessorRowId AND c.Position<=@Cut
GROUP BY c.MetricInputStreamRowId,f.ProductionDaySiteId,f.ProductionBusinessDate,
         f.MetricInputKey,f.Unit,f.IsPlannedProductionTime
ORDER BY c.MetricInputStreamRowId,f.ProductionDaySiteId,f.ProductionBusinessDate,
         f.MetricInputKey,f.Unit,f.IsPlannedProductionTime;

SELECT TOP (@PageSize) N'Q2B-fact-lineage-page' AS ResultSet,
       c.Position AS ContributionPosition,c.MetricInputStreamRowId AS ContributionStreamRowId,
       c.MetricInputFactRowId AS ContributionFactRowId,f.MachineId,
       f.FactId,f.FactIdBinary,f.MetricInputKey,f.MetricValue,f.Unit,
       f.StartsAtUtc,f.EndsAtUtc,f.IsPlannedProductionTime,
       f.PlannedProductionScheduleAssignmentId,
       f.SourceContextualizedActivityIntervalId,f.SourceEligibilityIntervalId,f.SourceQuantityEvidenceId,
       f.CompanyId,f.SiteId,f.ProductionLineId,f.ShiftId,f.ShiftScheduleAssignmentId,
       f.OccurrenceSiteId,f.OccurrenceShiftScheduleAssignmentId,f.OccurrenceShiftId,
       f.OccurrenceStartsAtUtc,f.OccurrenceEndsAtUtc,
       f.ProductionDaySiteId,f.ProductionBusinessDate
FROM dbo.MetricAggregationContribution c LEFT JOIN dbo.MetricInputFact f
 ON f.MetricInputFactRowId=c.MetricInputFactRowId
 AND f.MetricInputStreamRowId=c.MetricInputStreamRowId AND f.Position=c.Position
WHERE c.MetricAggregationProcessorRowId=@ProcessorRowId AND c.Position<=@Cut
 AND c.Position>@AfterPosition ORDER BY c.Position;
-- Next AfterPosition = greatest returned ContributionPosition; retain every page.
-- Repeat until an empty page. Reconcile unique cumulative positions to summary.
-- Do not filter only Running or a production date and call membership complete.
SELECT N'Q2B-page-progress' AS ResultSet,@AfterPosition AS RequestedAfterPosition,
 COUNT_BIG(*) AS RemainingContributionCount,
 N'PARTIAL until all unique positions reconcile to summary and a terminal empty page' AS InventoryStatus
FROM dbo.MetricAggregationContribution
WHERE MetricAggregationProcessorRowId=@ProcessorRowId AND Position<=@Cut AND Position>@AfterPosition;
SELECT N'Q2B-end-target' AS ResultSet,SERVERPROPERTY('ServerName') AS SqlServer,
       DB_NAME() AS DatabaseName,is_read_only
FROM sys.databases WHERE database_id=DB_ID();
