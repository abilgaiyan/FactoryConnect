CREATE NONCLUSTERED INDEX IX_MachineObservation_CncEvidence
ON dbo.MachineObservation (MachineId, StreamKeyBinary, Address, ObservedAt, Position)
INCLUDE (InstanceId, Quality);
