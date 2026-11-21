using FactoryConnect.Abstractions;
using FactoryConnect.Edge;

namespace FactoryConnect.GateBControlledAgent;

internal static class GateBPositiveAdmissionRehearsal
{
    public static async Task<int> RunAsync()
    {
        if (await GateBAdmissionPreflight.RunAsync() != 0)
            return 1;

        var machine = new MachineId(Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0"));
        var reporter = new GateBQualifiedGenerationReporter();
        var expected = new MtConnectContinuityLoss
        {
            MachineId = machine,
            Reason = MtConnectContinuityLossReason.InstanceChanged,
            PreviousInstanceId = 1793867780UL,
            CurrentInstanceId = 9000001UL,
            PreviousSequence = 27UL,
            RecoverySequence = 1UL,
        };
        await reporter.ReportAsync(expected);
        if (!reporter.Accepted)
            throw new InvalidDataException("Exact generation admission was not recorded.");

        var replayRejected = false;
        try { await reporter.ReportAsync(expected); }
        catch (InvalidDataException) { replayRejected = true; }
        if (!replayRejected)
            throw new InvalidDataException("Duplicate generation admission was accepted.");

        var unexpectedRejected = false;
        try
        {
            await new GateBQualifiedGenerationReporter().ReportAsync(expected with { CurrentInstanceId = 9000002UL });
        }
        catch (InvalidDataException) { unexpectedRejected = true; }
        if (!unexpectedRejected)
            throw new InvalidDataException("Unexpected source generation was accepted.");

        if (await GateBAdmissionPreflight.RunAsync() != 0)
            throw new InvalidDataException("SQL baseline changed during positive admission rehearsal.");

        Console.WriteLine("PASS: exact source generation qualified; duplicate and foreign generations rejected; SQL baseline unchanged.");
        Console.WriteLine("READ ONLY: qualification does not grant durable write authority.");
        return 0;
    }
}
