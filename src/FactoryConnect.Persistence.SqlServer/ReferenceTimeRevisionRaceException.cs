namespace FactoryConnect.Persistence.SqlServer;

internal sealed class ReferenceTimeRevisionRaceException()
    : InvalidOperationException("Reference-time publication revision advanced concurrently under the pending claim.");
