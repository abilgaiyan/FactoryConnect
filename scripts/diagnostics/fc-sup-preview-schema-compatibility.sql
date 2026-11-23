-- FC-SUP-01: inspect read-side schema before wiring a CNC counter reader.
-- READ ONLY. Run against the same local restored database as the diagnostic.
-- The repository's initial MachineObservation DDL has no Position column;
-- the local diagnostic succeeded with Position. Resolve this schema difference first.
SET NOCOUNT ON;
SELECT DB_NAME() AS DatabaseName,
       OBJECT_SCHEMA_NAME(t.object_id) AS TableSchema,
       t.name AS TableName,
       c.column_id AS ColumnOrdinal,
       c.name AS ColumnName,
       TYPE_NAME(c.user_type_id) AS SqlType,
       c.is_nullable AS IsNullable
FROM sys.tables AS t
JOIN sys.columns AS c ON c.object_id = t.object_id
WHERE t.name IN (N'MachineObservation', N'MachineShiftOccurrenceRoster',
                 N'MachineShiftOccurrenceRosterOccurrence')
ORDER BY t.name, c.column_id;

SELECT i.name AS IndexName, t.name AS TableName, ic.key_ordinal AS KeyOrdinal,
       c.name AS KeyColumn, i.is_unique AS IsUnique
FROM sys.tables AS t
JOIN sys.indexes AS i ON i.object_id = t.object_id
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c ON c.object_id = t.object_id AND c.column_id = ic.column_id
WHERE t.name IN (N'MachineObservation', N'MachineShiftOccurrenceRosterOccurrence')
  AND ic.key_ordinal > 0
ORDER BY t.name, i.name, ic.key_ordinal;

-- No observations or production evidence are changed.
