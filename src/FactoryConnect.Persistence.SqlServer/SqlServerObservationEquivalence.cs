using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlServerObservationEquivalence
{
    public static bool AreEquivalent(
        MachineObservation left,
        MachineObservation right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.MachineId == right.MachineId &&
            string.Equals(
                left.Source,
                right.Source,
                StringComparison.Ordinal) &&
            string.Equals(
                left.Address,
                right.Address,
                StringComparison.Ordinal) &&
            left.Type == right.Type &&
            SqlServerObservationValueCodec.AreEquivalent(
                left.Type,
                left.Value,
                right.Value) &&
            left.Quality == right.Quality &&
            left.Timestamp.Equals(right.Timestamp);
    }
}
