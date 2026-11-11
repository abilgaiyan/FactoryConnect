namespace FactoryConnect.GateBControlledAgent;

internal static class GateBTransactionalDenialRehearsal
{
    public static async Task<int> RunAsync()
    {
        foreach (var scenario in new[] { "stale-checkpoint", "stale-publication-head" })
        {
            var rejected = false;
            try
            {
                _ = await GateBTransactionalAdmissionRehearsal.RunAsync(
                    staleCheckpoint: scenario == "stale-checkpoint",
                    staleHead: scenario == "stale-publication-head");
            }
            catch (InvalidDataException error) when (
                error.Message is "Transactional acquisition checkpoint mismatch."
                    or "Transactional publication head mismatch.")
            {
                rejected = true;
            }

            if (!rejected)
                throw new InvalidDataException($"Adversarial admission was not denied: {scenario}.");
            if (await GateBAdmissionPreflight.RunAsync() != 0)
                throw new InvalidDataException($"Fixture baseline changed after {scenario} denial.");
            Console.WriteLine($"PASS: {scenario} rejected without durable SQL change.");
        }

        Console.WriteLine("READ ONLY: no acquisition commit was attempted.");
        return 0;
    }
}
