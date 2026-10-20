-- IMP-1C Q1 schema discovery only. Separate approval after Q0 review required.
DECLARE @ApprovedServer sysname = N'__APPROVED_SERVER__';
DECLARE @ApprovedDatabase sysname = N'__APPROVED_DATABASE__';
IF @ApprovedServer LIKE N'__APPROVED_%' OR @ApprovedDatabase LIKE N'__APPROVED_%'
    THROW 51000, 'Fill approved target identifiers only after execution authorization.', 1;
IF CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) COLLATE Latin1_General_100_BIN2
       <> @ApprovedServer COLLATE Latin1_General_100_BIN2
   OR DB_NAME() COLLATE Latin1_General_100_BIN2 <> @ApprovedDatabase COLLATE Latin1_General_100_BIN2
    THROW 51001, 'Connected target differs from approved disposable target.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases
               WHERE database_id = DB_ID() AND state_desc = N'ONLINE' AND is_read_only = 1)
    THROW 51002, 'Approved target must already be ONLINE and READ_ONLY.', 1;
IF ISNULL(IS_SRVROLEMEMBER('sysadmin'), 1) <> 0
   OR ISNULL(IS_ROLEMEMBER('db_owner'), 1) <> 0
    THROW 51003, 'Owner/admin or unverified role membership is not accepted.', 1;

-- Discovery searches known evidence families; it does not prove no other archive exists.
SELECT N'Q1-tables-columns' AS ResultSet,
       s.name AS SchemaName, t.name AS TableName, c.column_id,
       c.name AS ColumnName, ty.name AS TypeName, c.max_length,
       c.precision, c.scale, c.is_nullable
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE s.name = N'dbo' AND
      (t.name LIKE N'%Observation%' OR t.name LIKE N'%Acquisition%'
       OR t.name LIKE N'%Metric%' OR t.name LIKE N'%ProductionContext%'
       OR t.name LIKE N'%Eligibility%' OR t.name LIKE N'%Contextualized%'
       OR t.name LIKE N'%MachineActivity%' OR t.name LIKE N'%MachineState%'
       OR t.name LIKE N'%Schedule%' OR t.name LIKE N'%Roster%'
       OR t.name LIKE N'%Migration%')
ORDER BY s.name, t.name, c.column_id;

SELECT N'Q1-index-keys' AS ResultSet, s.name AS SchemaName, t.name AS TableName,
       i.name AS IndexName, i.is_unique, i.is_primary_key,
       ic.key_ordinal, ic.is_included_column, c.name AS ColumnName
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.indexes i ON i.object_id=t.object_id
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE s.name=N'dbo' AND t.name IN
      (N'MetricAggregationProcessor', N'MetricAggregationRevision',
       N'MetricAggregationContribution', N'MetricInputStream', N'MetricInputFact',
       N'ContextualizedActivityOutput', N'ProductionTimeEligibilityOutput',
       N'MachineActivityPeriodHistory', N'AcquisitionEvidencePublication',
       N'OperationalMetricCoverageSubject', N'OperationalMetricCoverageVersion')
ORDER BY t.name, i.name, ic.key_ordinal, ic.index_column_id;

SELECT N'Q1-visible-object-permissions' AS ResultSet,
       s.name AS SchemaName, t.name AS TableName, p.permission_name
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
CROSS APPLY sys.fn_my_permissions(QUOTENAME(s.name)+N'.'+QUOTENAME(t.name), 'OBJECT') p
WHERE s.name=N'dbo' AND t.name IN
      (N'MetricAggregationProcessor', N'MetricAggregationRevision',
       N'MetricAggregationContribution', N'MetricInputStream', N'MetricInputFact',
       N'ContextualizedActivityOutput', N'ProductionTimeEligibilityOutput',
       N'MachineActivityPeriodHistory', N'AcquisitionEvidencePublication',
       N'OperationalMetricCoverageSubject', N'OperationalMetricCoverageVersion')
ORDER BY t.name, p.permission_name;

SELECT N'Q1-end-target' AS ResultSet, SERVERPROPERTY('ServerName') AS SqlServer,
       DB_NAME() AS DatabaseName, is_read_only
FROM sys.databases WHERE database_id=DB_ID();
-- STOP. No application table rows have been read.
-- Freeze schema-aware evidence SELECTs in a separate review before Q2+ execution.
-- Missing catalog rows can reflect metadata visibility, not historical absence.
