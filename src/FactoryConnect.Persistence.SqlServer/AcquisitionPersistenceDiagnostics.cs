using System.Diagnostics;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Opt-in metadata-only spans; no SQL text, observation values or evidence payloads.</summary>
internal static class AcquisitionPersistenceDiagnostics
{
    internal static readonly ActivitySource Source = new("FactoryConnect.Acquisition");

    internal static Activity? Start(string name, ObservationStreamId stream)
    {
        var activity = Source.StartActivity(name);
        activity?.SetTag("machine.id", stream.MachineId.Value.ToString());
        activity?.SetTag("stream.key", stream.StreamKey);
        return activity;
    }

    internal sealed class VerificationWork
    {
        internal long SqlCommands { get; set; }
    }
}
