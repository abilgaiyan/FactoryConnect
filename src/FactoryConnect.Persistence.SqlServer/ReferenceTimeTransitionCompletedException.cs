namespace FactoryConnect.Persistence.SqlServer;

internal sealed class ReferenceTimeTransitionCompletedException()
    : InvalidOperationException("The reference-time publication transition completed concurrently.");
