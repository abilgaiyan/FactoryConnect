namespace FactoryConnect.GateBControlledAgent;

internal static class GateBWriterLeaseRehearsal
{
    public static async Task<int> RunAsync()
    {
        if (await GateBAdmissionPreflight.RunAsync() != 0)
            return 1;

        var connectionString = Environment.GetEnvironmentVariable("FC_GATEB_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Missing Gate B SQL connection string.");
        await using (var first = await GateBExclusiveWriterLease.AcquireAsync(connectionString))
        {
            var rejected = false;
            try
            {
                await using var second = await GateBExclusiveWriterLease.AcquireAsync(connectionString);
            }
            catch (InvalidOperationException error)
                when (error.Message == "Gate B fixture writer lease is already held or unavailable.")
            {
                rejected = true;
            }

            if (!rejected)
                throw new InvalidDataException("Competing Gate B writer acquired the exclusive lease.");

            if (await GateBAdmissionPreflight.RunAsync() != 0)
                throw new InvalidDataException("Baseline changed while the lease was held.");
        }

        await using (var reacquired = await GateBExclusiveWriterLease.AcquireAsync(connectionString))
        {
            if (await GateBAdmissionPreflight.RunAsync() != 0)
                throw new InvalidDataException("Baseline changed after lease release.");
        }

        Console.WriteLine("PASS: fixture SQL writer lease excluded a second session and was reacquired after release.");
        Console.WriteLine("READ ONLY: SQL application locks only; durable acquisition writes remain BLOCKED.");
        return 0;
    }
}
