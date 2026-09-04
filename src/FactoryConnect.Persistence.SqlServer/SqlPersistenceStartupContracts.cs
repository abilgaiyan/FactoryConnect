namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlPersistenceStartupFailureKind
{
    MigrationOperationalFailure,
    VerificationOperationalFailure,
    DatabaseIncompatible
}

internal enum SqlPersistenceStartupStage
{
    Migration,
    Verification
}

internal sealed class SqlPersistenceStartupOptions
{
    public SqlPersistenceStartupOptions(TimeSpan lockTimeout)
    {
        _ = SqlMigrationLockTimeout.ToMilliseconds(lockTimeout);
        LockTimeout = lockTimeout;
    }

    public TimeSpan LockTimeout { get; }
}

internal sealed class SqlPersistenceStartupException : Exception
{
    private SqlPersistenceStartupException(
        SqlPersistenceStartupFailureKind failureKind,
        string message,
        SqlRuntimeCompatibilityResult? compatibilityResult,
        Exception? innerException)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        CompatibilityResult = compatibilityResult;
    }

    public SqlPersistenceStartupFailureKind FailureKind { get; }

    public SqlRuntimeCompatibilityResult? CompatibilityResult { get; }

    public static SqlPersistenceStartupException MigrationOperationalFailure(Exception innerException)
    {
        ValidateOperationalFailure(innerException);

        return new SqlPersistenceStartupException(
            SqlPersistenceStartupFailureKind.MigrationOperationalFailure,
            "SQL persistence migration failed during startup.",
            compatibilityResult: null,
            innerException);
    }

    public static SqlPersistenceStartupException VerificationOperationalFailure(Exception innerException)
    {
        ValidateOperationalFailure(innerException);

        return new SqlPersistenceStartupException(
            SqlPersistenceStartupFailureKind.VerificationOperationalFailure,
            "SQL persistence compatibility verification failed during startup.",
            compatibilityResult: null,
            innerException);
    }

    public static SqlPersistenceStartupException DatabaseIncompatible(
        SqlRuntimeCompatibilityResult compatibilityResult)
    {
        ArgumentNullException.ThrowIfNull(compatibilityResult);

        if (compatibilityResult.IsCompatible)
        {
            throw new ArgumentException(
                "Compatible runtime results cannot produce a database-incompatible startup failure.",
                nameof(compatibilityResult));
        }

        return new SqlPersistenceStartupException(
            SqlPersistenceStartupFailureKind.DatabaseIncompatible,
            "SQL persistence is not compatible with this FactoryConnect runtime.",
            compatibilityResult,
            innerException: null);
    }

    private static void ValidateOperationalFailure(Exception innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);

        if (innerException is OperationCanceledException)
        {
            throw new ArgumentException(
                "Startup cancellation must propagate and cannot be wrapped as an operational startup failure.",
                nameof(innerException));
        }
    }
}

internal static class SqlPersistenceStartupLogContract
{
    public static IReadOnlyList<string> AllowedStructuredFields { get; } =
    [
        "HostName",
        "PersistenceProvider",
        "StartupStage",
        "FailureKind",
        "CompatibilityClassification",
        "DiagnosticCode",
        "DecisionStage",
        "Artifact",
        "Expected",
        "Actual",
        "Detail"
    ];

    public static IReadOnlyList<string> ForbiddenConfigurationFragments { get; } =
    [
        "ConnectionString",
        "Password",
        "AccessToken",
        "Token",
        "Secret"
    ];
}
