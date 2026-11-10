using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.GateBControlledAgent;

/// <summary>Exercise the real session's instance-change detection without any durable writer.</summary>
internal static class GateBGenerationRehearsal
{
    public static async Task<int> RunAsync()
    {
        if (await GateBAdmissionPreflight.RunAsync() != 0)
            return 1;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var client = new MtConnectSampleClient(http);
            var endpoint = new MtConnectEndpoint(new Uri("http://127.0.0.1:51027/"));
            var machine = new MachineId(Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0"));
            const string device = "DEWAS-SR-MOXA-001";
            var original = new MtConnectAcquisitionSession(client, 1793867780UL, 27UL);

            MtConnectInstanceChangedException discontinuity;
            try
            {
                await original.PrepareNextAsync(endpoint, machine, device);
                throw new InvalidDataException("Fixture incorrectly appeared to continue the original MTConnect generation.");
            }
            catch (MtConnectInstanceChangedException error)
            {
                discontinuity = error;
            }

            if (discontinuity.PreviousInstanceId != 1793867780UL ||
                discontinuity.CurrentInstanceId != 9000001UL ||
                discontinuity.FirstSequence != 1UL ||
                original.NextSequence != 27UL || original.InstanceId != 1793867780UL)
                throw new InvalidDataException("Unexpected discontinuity or original session checkpoint drift.");

            var replacement = new MtConnectAcquisitionSession(client, discontinuity.FirstSequence);
            var result = await replacement.PrepareNextAsync(endpoint, machine, device);
            var observation = result.Observations.Single();
            if (result.InstanceId != 9000001UL || result.NextSequence != 2UL ||
                observation.Sequence != 1UL ||
                !string.Equals(observation.Observation.Value?.ToString(), "RUNNING", StringComparison.Ordinal))
                throw new InvalidDataException("New generation retry did not match controlled RUNNING fixture.");
            if (replacement.InstanceId is not null || replacement.NextSequence != 1UL)
                throw new InvalidDataException("Rehearsal advanced a session before durable persistence.");

            Console.WriteLine("PASS: original instance rejected; new instance detected; retry prepared at sequence 1.");
            Console.WriteLine("READ ONLY: neither session advanced; no continuity report or durable SQL commit executed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }
}
