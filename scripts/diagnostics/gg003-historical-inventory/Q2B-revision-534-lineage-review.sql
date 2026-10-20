-- Q2B.1 revision-534 candidate. REVIEW ONLY; NOT execution authorization.
DECLARE @Server sysname=N'DESKTOP-1PJK2OB\FCINVENTORY';
DECLARE @Database sysname=N'FactoryConnect_GG003_IMP1C_INVENTORY_20261020';
DECLARE @Machine uniqueidentifier='677b030b-1ede-4a67-82ae-6a494791357f';
DECLARE @Processor bigint=1, @Stream bigint=1, @Cut decimal(20,0)=534;
DECLARE @MaxMembers bigint=1000;
IF CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) COLLATE Latin1_General_100_BIN2<>@Server COLLATE Latin1_General_100_BIN2
 OR DB_NAME() COLLATE Latin1_General_100_BIN2<>@Database COLLATE Latin1_General_100_BIN2
 THROW 51200,'Q2B target mismatch.',1;
IF NOT EXISTS(SELECT 1 FROM sys.databases WHERE database_id=DB_ID() AND state_desc='ONLINE' AND is_read_only=1)
 THROW 51201,'Q2B requires unchanged ONLINE READ_ONLY target.',1;
IF ISNULL(IS_SRVROLEMEMBER('sysadmin'),1)<>0 OR ISNULL(IS_ROLEMEMBER('db_owner'),1)<>0
 THROW 51202,'Restricted-reader role check failed/indeterminate.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.MetricAggregationProcessor p
 JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=p.MetricInputStreamRowId
 JOIN dbo.MetricAggregationRevision r ON r.MetricAggregationProcessorRowId=p.MetricAggregationProcessorRowId
 WHERE p.MetricAggregationProcessorRowId=@Processor AND s.MetricInputStreamRowId=@Stream
 AND s.MachineId=@Machine AND s.StreamKey COLLATE Latin1_General_100_BIN2=N'metric-inputs'
 AND p.ProcessorKey COLLATE Latin1_General_100_BIN2=N'metric-aggregation:677b030b-1ede-4a67-82ae-6a494791357f'
 AND r.Position=@Cut)
 THROW 51203,'Exact committed machine/processor/stream cut absent.',1;
IF (SELECT COUNT_BIG(*) FROM dbo.MetricAggregationContribution
 WHERE MetricAggregationProcessorRowId=@Processor AND Position<=@Cut)>@MaxMembers
 THROW 51204,'Reviewed membership bound exceeded; no truncation.',1;

SELECT N'Q2B-target' AS ResultSet,SERVERPROPERTY('ServerName') AS SqlServer,
 DB_NAME() AS DatabaseName,ORIGINAL_LOGIN() AS OriginalLogin,USER_NAME() AS DatabaseUser,
 is_read_only,SYSDATETIMEOFFSET() AS CapturedAt
FROM sys.databases WHERE database_id=DB_ID();
SELECT N'Q2B-cut' AS ResultSet,p.MetricAggregationProcessorRowId,p.ProcessorKey,
 s.MetricInputStreamRowId,s.StreamKey,s.MachineId,r.Position AS CommittedThroughMetricInputPosition
FROM dbo.MetricAggregationProcessor p JOIN dbo.MetricInputStream s
 ON s.MetricInputStreamRowId=p.MetricInputStreamRowId
JOIN dbo.MetricAggregationRevision r ON r.MetricAggregationProcessorRowId=p.MetricAggregationProcessorRowId
WHERE p.MetricAggregationProcessorRowId=@Processor AND r.Position=@Cut;

SELECT N'Q2B-contribution-boundary' AS ResultSet,COUNT_BIG(*) AS AdmittedContributionCount,
 MIN(c.Position) AS FirstAdmittedPosition,MAX(c.Position) AS LastAdmittedPosition,
 (SELECT COUNT_BIG(*) FROM dbo.MetricInputFact WHERE MetricInputStreamRowId=@Stream AND Position<=@Cut)
 AS StoredFactsThroughPositionDiagnosticOnly,
 (SELECT COUNT_BIG(*) FROM dbo.MetricInputFact f WHERE f.MetricInputStreamRowId=@Stream AND f.Position<=@Cut
  AND NOT EXISTS(SELECT 1 FROM dbo.MetricAggregationContribution m
   WHERE m.MetricAggregationProcessorRowId=@Processor AND m.Position<=@Cut
   AND m.MetricInputFactRowId=f.MetricInputFactRowId AND m.MetricInputStreamRowId=f.MetricInputStreamRowId
   AND m.Position=f.Position)) AS StoredButNotExactMembersDiagnosticOnly
FROM dbo.MetricAggregationContribution c WHERE c.MetricAggregationProcessorRowId=@Processor AND c.Position<=@Cut;

SELECT N'Q2B-fact-membership' AS ResultSet,c.Position AS ContributionPosition,
 c.MetricInputStreamRowId AS ContributionStream,c.MetricInputFactRowId AS ContributionFactRow,
 f.MachineId,f.FactId,f.FactIdBinary,f.Position AS FactPosition,f.MetricInputKey,f.MetricValue,f.Unit,
 f.StartsAtUtc,f.EndsAtUtc,f.IsPlannedProductionTime,f.PlannedProductionScheduleAssignmentId,
 f.SourceEligibilityIntervalId,f.SourceContextualizedActivityIntervalId,
 f.CompanyId,f.SiteId,f.ProductionLineId,f.ProductionContextAssignmentId,
 f.ProductionOrderId,f.OperationId,f.PartId,f.OperatorId,
 f.OccurrenceSiteId,f.OccurrenceShiftScheduleAssignmentId,f.OccurrenceShiftId,
 f.OccurrenceStartsAtUtc,f.OccurrenceEndsAtUtc,f.ProductionDaySiteId,f.ProductionBusinessDate
FROM dbo.MetricAggregationContribution c LEFT JOIN dbo.MetricInputFact f
 ON f.MetricInputFactRowId=c.MetricInputFactRowId AND f.MetricInputStreamRowId=c.MetricInputStreamRowId AND f.Position=c.Position
WHERE c.MetricAggregationProcessorRowId=@Processor AND c.Position<=@Cut ORDER BY c.Position;

-- Membership groups, NOT sums or a replacement Availability. Original values are above.
SELECT N'Q2B-duration-operands' AS ResultSet,f.ProductionDaySiteId,f.ProductionBusinessDate,
 f.OccurrenceSiteId,f.OccurrenceShiftScheduleAssignmentId,f.OccurrenceShiftId,
 f.OccurrenceStartsAtUtc,f.OccurrenceEndsAtUtc,f.MetricInputKey,f.Unit,f.IsPlannedProductionTime,
 COUNT_BIG(*) AS AdmittedFactCount,MIN(c.Position) AS FirstPosition,MAX(c.Position) AS LastPosition
FROM dbo.MetricAggregationContribution c JOIN dbo.MetricInputFact f
 ON f.MetricInputFactRowId=c.MetricInputFactRowId AND f.MetricInputStreamRowId=c.MetricInputStreamRowId AND f.Position=c.Position
WHERE c.MetricAggregationProcessorRowId=@Processor AND c.Position<=@Cut
 AND f.MetricInputKey IN(N'duration.running',N'duration.scheduled',N'duration.stopped',N'duration.planned-production')
GROUP BY f.ProductionDaySiteId,f.ProductionBusinessDate,f.OccurrenceSiteId,
 f.OccurrenceShiftScheduleAssignmentId,f.OccurrenceShiftId,f.OccurrenceStartsAtUtc,f.OccurrenceEndsAtUtc,
 f.MetricInputKey,f.Unit,f.IsPlannedProductionTime
ORDER BY f.ProductionDaySiteId,f.ProductionBusinessDate,f.OccurrenceStartsAtUtc,f.MetricInputKey,f.IsPlannedProductionTime;

SELECT N'Q2B-coverage' AS ResultSet,COUNT_BIG(*) AS ContributionCount,
 COUNT_BIG(CASE WHEN rawf.MetricInputFactRowId IS NULL THEN 1 END) AS ReferencedFactAbsent,
 COUNT_BIG(CASE WHEN f.MetricInputFactRowId IS NULL THEN 1 END) AS ExactJoinMissing,
 COUNT_BIG(CASE WHEN c.MetricInputStreamRowId<>@Stream THEN 1 END) AS WrongContributionStream,
 COUNT_BIG(CASE WHEN rawf.MetricInputFactRowId IS NOT NULL AND
  (rawf.MetricInputStreamRowId<>c.MetricInputStreamRowId OR rawf.Position<>c.Position) THEN 1 END) AS FactBindingMismatch,
 COUNT_BIG(CASE WHEN rawf.MachineId<>@Machine THEN 1 END) AS WrongExistingFactMachine,
 COUNT_BIG(CASE WHEN rawf.MetricInputFactRowId IS NULL THEN 1 END) AS FactMachineUnverifiable,
 COUNT_BIG(CASE WHEN f.MetricInputFactRowId IS NOT NULL AND f.SourceEligibilityIntervalId IS NULL THEN 1 END) AS NullEligibilityReferences,
 COUNT_BIG(CASE WHEN f.MetricInputFactRowId IS NOT NULL AND f.SourceContextualizedActivityIntervalId IS NULL THEN 1 END) AS NullContextReferences,
 (SELECT COUNT_BIG(*) FROM (SELECT Position FROM dbo.MetricAggregationContribution
  WHERE MetricAggregationProcessorRowId=@Processor AND Position<=@Cut GROUP BY Position HAVING COUNT_BIG(*)>1) d) AS DuplicatePositionGroups,
 (SELECT COUNT_BIG(*) FROM (SELECT MetricInputFactRowId FROM dbo.MetricAggregationContribution
  WHERE MetricAggregationProcessorRowId=@Processor AND Position<=@Cut GROUP BY MetricInputFactRowId HAVING COUNT_BIG(*)>1) d) AS DuplicateFactRowGroups
FROM dbo.MetricAggregationContribution c
LEFT JOIN dbo.MetricInputFact rawf ON rawf.MetricInputFactRowId=c.MetricInputFactRowId
LEFT JOIN dbo.MetricInputFact f ON f.MetricInputFactRowId=c.MetricInputFactRowId
 AND f.MetricInputStreamRowId=c.MetricInputStreamRowId AND f.Position=c.Position
WHERE c.MetricAggregationProcessorRowId=@Processor AND c.Position<=@Cut;
SELECT N'Q2B-end-target' AS ResultSet,SERVERPROPERTY('ServerName') AS SqlServer,
 DB_NAME() AS DatabaseName,is_read_only FROM sys.databases WHERE database_id=DB_ID();
