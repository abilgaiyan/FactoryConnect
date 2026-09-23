using System.Collections.Concurrent;
using System.Globalization;
using System.Security;

namespace FactoryConnect.Rehearsal.MTConnectFixture;

public sealed class RehearsalMtConnectState
{
    private sealed class MachineState
    {
        public object Gate { get; } = new();
        public ulong NextSequence { get; set; } = 1;
    }

    private readonly ConcurrentDictionary<string, MachineState> _states =
        new(StringComparer.Ordinal);

    public string GetProbeDocument(RehearsalMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        return $"""
            <MTConnectDevices xmlns="urn:mtconnect.org:MTConnectDevices:2.5">
              <Header instanceId="{RehearsalFixtureTopology.InstanceId}" version="2.5.0" />
              <Devices>
                <Device id="device-{machine.Ordinal}" name="{Xml(machine.DeviceKey)}" uuid="{Xml(machine.DeviceUuid)}">
                  <Components>
                    <Controller id="controller-{machine.Ordinal}" name="controller">
                      <DataItems>
                        <DataItem id="exec" name="execution" category="EVENT" type="EXECUTION" />
                        <DataItem id="part_count" name="part_count" category="SAMPLE" type="PART_COUNT" />
                      </DataItems>
                    </Controller>
                  </Components>
                  <DataItems>
                    <DataItem id="avail" name="availability" category="EVENT" type="AVAILABILITY" />
                  </DataItems>
                </Device>
              </Devices>
            </MTConnectDevices>
            """;
    }

    public string GetCurrentDocument(RehearsalMachine machine)
    {
        var state = GetState(machine);
        lock (state.Gate)
        {
            var sequence = state.NextSequence++;
            return BuildStreamsDocument(machine, sequence, sequence);
        }
    }

    public string GetSampleDocument(RehearsalMachine machine, ulong fromSequence)
    {
        var state = GetState(machine);
        lock (state.Gate)
        {
            if (fromSequence >= state.NextSequence)
            {
                state.NextSequence = fromSequence + 1;
            }

            var first = state.NextSequence++;
            var last = state.NextSequence++;
            return BuildStreamsDocument(machine, first, last);
        }
    }

    private MachineState GetState(RehearsalMachine machine) =>
        _states.GetOrAdd(machine.DeviceKey, static _ => new MachineState());

    private static string BuildStreamsDocument(
        RehearsalMachine machine,
        ulong firstObservationSequence,
        ulong lastObservationSequence)
    {
        var firstSequence = 1UL;
        var nextSequence = checked(lastObservationSequence + 1);
        var running = lastObservationSequence % 4 is 1 or 2;
        var execution = running ? "ACTIVE" : "READY";
        var partCount = lastObservationSequence / 2;
        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var secondObservation = lastObservationSequence == firstObservationSequence
            ? string.Empty
            : $"""

                        <PartCount dataItemId="part_count" timestamp="{timestamp}" sequence="{lastObservationSequence}">{partCount}</PartCount>
              """;

        return $"""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="{RehearsalFixtureTopology.InstanceId}"
                      firstSequence="{firstSequence}"
                      lastSequence="{lastObservationSequence}"
                      nextSequence="{nextSequence}" />
              <Streams>
                <DeviceStream name="{Xml(machine.DeviceKey)}" uuid="{Xml(machine.DeviceUuid)}">
                  <ComponentStream component="Controller" componentId="controller-{machine.Ordinal}">
                    <Events>
                      <Execution dataItemId="exec" timestamp="{timestamp}" sequence="{firstObservationSequence}">{execution}</Execution>
                      <Availability dataItemId="avail" timestamp="{timestamp}" sequence="{firstObservationSequence}">AVAILABLE</Availability>
                    </Events>
                    <Samples>{secondObservation}
                    </Samples>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """;
    }

    private static string Xml(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
