using System.Collections.Immutable;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlPersistenceStartupContractTests
{
    private static readonly string[] ExpectedFailureKinds =
    [
        "MigrationOperationalFailure",
        "VerificationOperationalFailure",
        "DatabaseIncompatible"
    ];

    private static readonly string[] ExpectedStages =
    [
        "Migration",
        "Verification"
    ];

    private static readonly string[] ExpectedLogFields =
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

    [Fact]
    public void PersistenceStartupGateExposesOneCancellationAwareReadinessOperation()
    {
        var methods = typeof(IPersistenceStartupGate).GetMethods();

        var method = Assert.Single(methods);
        Assert.Equal("EnsureReadyAsync", method.Name);
        Assert.Equal(typeof(ValueTask), method.ReturnType);

        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(CancellationToken), parameter.ParameterType);
    }

    [Fact]
    public void FailureVocabularyIsClosedAndOrdered()
    {
        Assert.Equal(ExpectedFailureKinds, Enum.GetNames<SqlPersistenceStartupFailureKind>());
    }

    [Fact]
    public void StartupStageVocabularyIsClosedAndOrdered()
    {
        Assert.Equal(ExpectedStages, Enum.GetNames<SqlPersistenceStartupStage>());
    }

    [Fact]
    public void StartupOptionsRetainExactConfiguredLockTimeout()
    {
        var timeout = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1);

        var options = new SqlPersistenceStartupOptions(timeout);

        Assert.Equal(timeout, options.LockTimeout);
        Assert.Equal(2, SqlMigrationLockTimeout.ToMilliseconds(options.LockTimeout));
    }

    [Fact]
    public void StartupOptionsAcceptZeroWaitThroughFrozenTimeoutAuthority()
    {
        var options = new SqlPersistenceStartupOptions(TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, options.LockTimeout);
        Assert.Equal(0, SqlMigrationLockTimeout.ToMilliseconds(options.LockTimeout));
    }

    [Fact]
    public void StartupOptionsRejectNegativeTimeoutThroughFrozenTimeoutAuthority()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqlPersistenceStartupOptions(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void StartupOptionsRejectTimeoutBeyondSqlIntLimitThroughFrozenTimeoutAuthority()
    {
        var tooLarge = TimeSpan.FromTicks(
            checked(((long)int.MaxValue * TimeSpan.TicksPerMillisecond) + 1));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqlPersistenceStartupOptions(tooLarge));
    }

    [Fact]
    public void MigrationOperationalFailurePreservesExactCauseWithoutCompatibilityResult()
    {
        var cause = new InvalidOperationException("provider detail should remain only on the cause");

        var failure = SqlPersistenceStartupException.MigrationOperationalFailure(cause);

        Assert.Equal(SqlPersistenceStartupFailureKind.MigrationOperationalFailure, failure.FailureKind);
        Assert.Same(cause, failure.InnerException);
        Assert.Null(failure.CompatibilityResult);
        Assert.Equal("SQL persistence migration failed during startup.", failure.Message);
        Assert.DoesNotContain(cause.Message, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationOperationalFailurePreservesExactCauseWithoutCompatibilityResult()
    {
        var cause = new InvalidOperationException("provider detail should remain only on the cause");

        var failure = SqlPersistenceStartupException.VerificationOperationalFailure(cause);

        Assert.Equal(SqlPersistenceStartupFailureKind.VerificationOperationalFailure, failure.FailureKind);
        Assert.Same(cause, failure.InnerException);
        Assert.Null(failure.CompatibilityResult);
        Assert.Equal("SQL persistence compatibility verification failed during startup.", failure.Message);
        Assert.DoesNotContain(cause.Message, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyOperationCanceledExceptionMustPropagateRegardlessOfSuppliedTokenState()
    {
        using var source = new CancellationTokenSource();
        var unrelatedCancellation = new OperationCanceledException();
        var suppliedTokenCancellation = new OperationCanceledException(source.Token);

        Assert.False(source.IsCancellationRequested);
        Assert.True(SqlPersistenceStartupCancellationPolicy.MustPropagate(unrelatedCancellation));
        Assert.True(SqlPersistenceStartupCancellationPolicy.MustPropagate(suppliedTokenCancellation));
        Assert.False(SqlPersistenceStartupCancellationPolicy.MustPropagate(new InvalidOperationException()));
    }

    [Fact]
    public void MigrationOperationalFailureRejectsCancellationWrapping()
    {
        Assert.Throws<ArgumentException>(
            () => SqlPersistenceStartupException.MigrationOperationalFailure(
                new OperationCanceledException()));
    }

    [Fact]
    public void VerificationOperationalFailureRejectsCancellationWrapping()
    {
        Assert.Throws<ArgumentException>(
            () => SqlPersistenceStartupException.VerificationOperationalFailure(
                new OperationCanceledException()));
    }

    [Fact]
    public void DatabaseIncompatiblePreservesExactRuntimeCompatibilityResult()
    {
        var result = CreateIncompatibleResult();

        var failure = SqlPersistenceStartupException.DatabaseIncompatible(result);

        Assert.Equal(SqlPersistenceStartupFailureKind.DatabaseIncompatible, failure.FailureKind);
        Assert.Same(result, failure.CompatibilityResult);
        Assert.Null(failure.InnerException);
        Assert.Equal(
            "SQL persistence is not compatible with this FactoryConnect runtime.",
            failure.Message);
    }

    [Fact]
    public void DatabaseIncompatibleRejectsCompatibleResult()
    {
        var compatible = new SqlRuntimeCompatibilityResult(
            SqlRuntimeCompatibilityClassification.Compatible,
            ImmutableArray<SqlRuntimeCompatibilityDiagnostic>.Empty);

        Assert.Throws<ArgumentException>(
            () => SqlPersistenceStartupException.DatabaseIncompatible(compatible));
    }

    [Fact]
    public void StructuredLoggingVocabularyIsClosedAndSecretSafe()
    {
        Assert.Equal(ExpectedLogFields, SqlPersistenceStartupLogContract.AllowedStructuredFields);

        foreach (var field in SqlPersistenceStartupLogContract.AllowedStructuredFields)
        {
            Assert.DoesNotContain(
                SqlPersistenceStartupLogContract.ForbiddenConfigurationFragments,
                fragment => field.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static SqlRuntimeCompatibilityResult CreateIncompatibleResult()
    {
        var diagnostic = new SqlRuntimeCompatibilityDiagnostic(
            SqlRuntimeCompatibilityDiagnosticCode.MigrationPending,
            SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship,
            "Migration:004_ProductionContextMetricInputHandoff",
            expected: "present",
            actual: "missing",
            detail: "Repository migration is pending.");

        return new SqlRuntimeCompatibilityResult(
            SqlRuntimeCompatibilityClassification.MigrationPending,
            ImmutableArray.Create(diagnostic));
    }
}
