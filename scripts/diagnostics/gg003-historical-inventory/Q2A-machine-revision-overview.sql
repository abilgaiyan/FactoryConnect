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

DECLARE @FromDate date='20261016', @ToDateExclusive date='20261020';
-- Activity window is a discovery envelope, not a production-day authority:
-- 16 Oct 00:00 IST through 20 Oct 06:00 IST, stored and reported in UTC.
DECLARE @FromUtc datetimeoffset(7)='2026-10-15T18:30:00+00:00';
DECLARE @ToUtc datetimeoffset(7)='2026-10-20T00:30:00+00:00';
DECLARE @MaxStreams int=100, @MaxProcessors int=100;
DECLARE @AfterProcessorRowId bigint=0, @AfterRevision decimal(20,0)=0, @RevisionPageSize int=500;
DECLARE @IncludeWindowCounts bit=0; -- Enable only after separate index/plan/resource review.
IF @AfterProcessorRowId<0 OR @AfterRevision<0 OR @AfterRevision>18446744073709551615
 OR @RevisionPageSize<1 OR @RevisionPageSize>500
 THROW 51105, 'Invalid revision cursor/page size.', 1;
IF (SELECT COUNT_BIG(*) FROM dbo.MetricInputStream WHERE MachineId=@MachineId)>@MaxStreams
 OR (SELECT COUNT_BIG(*) FROM dbo.MetricAggregationProcessor p
     JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=p.MetricInputStreamRowId
     WHERE s.MachineId=@MachineId)>@MaxProcessors
    THROW 51102, 'Identity scope exceeds reviewed cap; do not silently truncate.', 1;
SELECT N'Q2A-target' AS ResultSet, SERVERPROPERTY('ServerName') AS SqlServer,
       DB_NAME() AS DatabaseName, @MachineId AS MachineId, SYSDATETIMEOFFSET() AS CapturedAt,
       @FromDate AS FromBusinessDate, @ToDateExclusive AS ToBusinessDateExclusive,
       @FromUtc AS ActivityEnvelopeFromUtc, @ToUtc AS ActivityEnvelopeToUtc;

SELECT N'Q2A-identities' AS ResultSet,s.MetricInputStreamRowId,s.MachineId,
       s.StreamKey,s.StreamKeyBinary,p.MetricAggregationProcessorRowId,
       p.ProcessorKey,p.ProcessorKeyBinary,c.Position AS CurrentAggregationCheckpointDiagnostic
FROM dbo.MetricInputStream s
LEFT JOIN dbo.MetricAggregationProcessor p ON p.MetricInputStreamRowId=s.MetricInputStreamRowId
LEFT JOIN dbo.MetricAggregationCheckpoint c ON c.MetricAggregationProcessorRowId=p.MetricAggregationProcessorRowId
WHERE s.MachineId=@MachineId
ORDER BY s.MetricInputStreamRowId,p.MetricAggregationProcessorRowId;

SELECT N'Q2A-revision-inventory' AS ResultSet,p.MetricAggregationProcessorRowId,
 COUNT_BIG(*) AS TotalCommittedRevisions,MIN(r.Position) AS FirstRevision,MAX(r.Position) AS LastRevision
FROM dbo.MetricAggregationRevision r
JOIN dbo.MetricAggregationProcessor p ON p.MetricAggregationProcessorRowId=r.MetricAggregationProcessorRowId
JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=p.MetricInputStreamRowId
WHERE s.MachineId=@MachineId GROUP BY p.MetricAggregationProcessorRowId ORDER BY p.MetricAggregationProcessorRowId;
SELECT TOP (@RevisionPageSize) N'Q2A-committed-revisions' AS ResultSet,p.MetricAggregationProcessorRowId,
       s.MetricInputStreamRowId,p.ProcessorKey,r.Position,
       CASE WHEN r.Position=534 THEN 1 ELSE 0 END AS IsRcaCandidate534,
       (SELECT COUNT_BIG(*) FROM dbo.MetricAggregationRevisionProductionDay d
        WHERE d.MetricAggregationProcessorRowId=r.MetricAggregationProcessorRowId
          AND d.Position=r.Position AND d.ProductionBusinessDate>=@FromDate
          AND d.ProductionBusinessDate<@ToDateExclusive) AS SelectedChangedDayCount
FROM dbo.MetricAggregationRevision r
JOIN dbo.MetricAggregationProcessor p ON p.MetricAggregationProcessorRowId=r.MetricAggregationProcessorRowId
JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=p.MetricInputStreamRowId
WHERE s.MachineId=@MachineId
 AND (p.MetricAggregationProcessorRowId>@AfterProcessorRowId OR
 (p.MetricAggregationProcessorRowId=@AfterProcessorRowId AND r.Position>@AfterRevision))
ORDER BY p.MetricAggregationProcessorRowId,r.Position;
-- Revision 534 is a candidate only. ChangedDayCount=0 does not exclude an as-of
-- historical cut; changed-period ledger is not a complete per-cut coverage manifest.

SELECT N'Q2A-fact-counts' AS ResultSet,s.MetricInputStreamRowId,
       f.ProductionDaySiteId,f.ProductionBusinessDate,f.MetricInputKey,f.Unit,
       f.IsPlannedProductionTime,COUNT_BIG(*) AS FactCount,
       MIN(f.Position) AS FirstMetricInputPosition,MAX(f.Position) AS LastMetricInputPosition,
       MIN(f.StartsAtUtc) AS EarliestFactStartUtc,MAX(f.EndsAtUtc) AS LatestFactEndUtc
FROM dbo.MetricInputFact f JOIN dbo.MetricInputStream s
 ON s.MetricInputStreamRowId=f.MetricInputStreamRowId
WHERE s.MachineId=@MachineId AND f.MachineId=@MachineId
 AND f.ProductionBusinessDate>=@FromDate AND f.ProductionBusinessDate<@ToDateExclusive
GROUP BY s.MetricInputStreamRowId,f.ProductionDaySiteId,f.ProductionBusinessDate,
         f.MetricInputKey,f.Unit,f.IsPlannedProductionTime
ORDER BY s.MetricInputStreamRowId,f.ProductionDaySiteId,f.ProductionBusinessDate,
         f.MetricInputKey,f.Unit,f.IsPlannedProductionTime;

IF @IncludeWindowCounts=1
BEGIN
SELECT N'Q2A-raw-window-counts' AS ResultSet,StreamKeyBinary,COUNT_BIG(*) AS ObservationCount,
       MIN(Position) AS FirstObservationPosition,MAX(Position) AS LastObservationPosition,
       MIN(ObservedAt) AS FirstObservedAt,MAX(ObservedAt) AS LastObservedAt
FROM dbo.MachineObservation
WHERE MachineId=@MachineId AND ObservedAt>=@FromUtc AND ObservedAt<@ToUtc
GROUP BY StreamKeyBinary ORDER BY StreamKeyBinary;
SELECT N'Q2A-activity-window-counts' AS ResultSet,StreamKeyBinary,StateProcessorIdOrderKey,
       MachineState,COUNT_BIG(*) AS IntersectingPeriodCount,
       MIN(StartedAt) AS EarliestStoredStart,MAX(EndedAt) AS LatestStoredEnd
FROM dbo.MachineActivityPeriodHistory
WHERE MachineId=@MachineId AND StartedAt<@ToUtc AND EndedAt>@FromUtc
GROUP BY StreamKeyBinary,StateProcessorIdOrderKey,MachineState
ORDER BY StreamKeyBinary,StateProcessorIdOrderKey,MachineState;
END
ELSE SELECT N'Q2A-window-counts-held' AS ResultSet,N'Not executed; separate plan/resource approval required' AS Status;
SELECT N'Q2A-revision-page-request' AS ResultSet,@AfterProcessorRowId AS AfterProcessorRowId,
 @AfterRevision AS AfterRevision,@RevisionPageSize AS PageSize,
 N'INCOMPLETE until unique keys reconcile to totals and terminal empty page' AS MembershipStatus;
-- Counts and envelope extrema are discovery diagnostics, never completeness proof.
SELECT N'Q2A-end-target' AS ResultSet,SERVERPROPERTY('ServerName') AS SqlServer,
       DB_NAME() AS DatabaseName,is_read_only
FROM sys.databases WHERE database_id=DB_ID();
