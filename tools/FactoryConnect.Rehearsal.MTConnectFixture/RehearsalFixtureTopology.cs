namespace FactoryConnect.Rehearsal.MTConnectFixture;

public sealed record RehearsalMachine(
    int Ordinal,
    string MachineId,
    string DeviceKey,
    string DeviceUuid,
    string BasePath,
    string StreamIdentity,
    string ProcessorId,
    string SiteId,
    string ProductionLineId);

public static class RehearsalFixtureTopology
{
    public const ulong InstanceId = 2026092301UL;

    public static IReadOnlyList<RehearsalMachine> Machines { get; } =
    [
        new(1, "11111111-1111-1111-1111-111111111101", "CNC-01", "fc-rehearsal-cnc-01", "/mtconnect/cnc-01/", "mtconnect:CNC-01", "processor-cnc-01", "GAJRA-NEW", "LINE-1"),
        new(2, "11111111-1111-1111-1111-111111111102", "CNC-02", "fc-rehearsal-cnc-02", "/mtconnect/cnc-02/", "mtconnect:CNC-02", "processor-cnc-02", "GAJRA-NEW", "LINE-1"),
        new(3, "11111111-1111-1111-1111-111111111103", "CNC-03", "fc-rehearsal-cnc-03", "/mtconnect/cnc-03/", "mtconnect:CNC-03", "processor-cnc-03", "GAJRA-NEW", "LINE-1"),
        new(4, "11111111-1111-1111-1111-111111111104", "CNC-04", "fc-rehearsal-cnc-04", "/mtconnect/cnc-04/", "mtconnect:CNC-04", "processor-cnc-04", "GAJRA-NEW", "LINE-1"),
        new(5, "11111111-1111-1111-1111-111111111105", "CNC-05", "fc-rehearsal-cnc-05", "/mtconnect/cnc-05/", "mtconnect:CNC-05", "processor-cnc-05", "GAJRA-NEW", "LINE-1"),
        new(6, "11111111-1111-1111-1111-111111111106", "CNC-06", "fc-rehearsal-cnc-06", "/mtconnect/cnc-06/", "mtconnect:CNC-06", "processor-cnc-06", "GAJRA-NEW", "LINE-2"),
        new(7, "11111111-1111-1111-1111-111111111107", "CNC-07", "fc-rehearsal-cnc-07", "/mtconnect/cnc-07/", "mtconnect:CNC-07", "processor-cnc-07", "GAJRA-NEW", "LINE-2")
    ];
}
