using System.Collections.Immutable;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlPersistenceStartupGateCompositionTests
{
    [Fact]
    public async Task SelectedProviderOwnsStartupGateFactory()
    {
        var selectedGate = new ProbeStartupGate();
        var unselectedGate = new ProbeStartupGate();
        var services = new ServiceCollection();
        services.AddPersistenceProvider(CreateRegistration("A", unselectedGate));
        services.AddPersistenceProvider(CreateRegistration("B", selectedGate));

        var configuration = BuildConfiguration(("Persistence:Provider", "B"));
        services.AddFactoryConnectPersistence(configuration);

        await using var provider = services.BuildServiceProvider();
        var actual = provider.GetRequiredService<IPersistenceStartupGate>();

        Assert.Same(selectedGate, actual);
        Assert.Equal(0, unselectedGate.InvocationCount);
    }

    [Fact]
    public async Task InMemorySelectionDoesNotRequireOrConstructSqlStartupGate()
    {
        var configuration = BuildConfiguration(("Persistence:Provider", "InMemory"));
        var services = new ServiceCollection();
        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(configuration);

        await using var provider = services.BuildServiceProvider();
        var gate = provider.GetRequiredService<IPersistenceStartupGate>();

        await gate.EnsureReadyAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DefaultProviderStartupGateObservesCancellation()
    {
        var registration = new PersistenceProviderRegistration(
            "Provider",
            PersistenceProviderCapabilities.Core,
            static _ => throw new InvalidOperationException("Store factory must not be invoked."));
        using var services = new ServiceCollection().BuildServiceProvider();
        var gate = registration.CreateStartupGate(services);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await gate.EnsureReadyAsync(cancellation.Token));
    }

    [Fact]
    public async Task SqlGateRunsMigrationThenVerificationWithSameConfiguredTimeout()
    {
        var order = new List<string>();
        var timeout = TimeSpan.FromMilliseconds(1234);
        var gate = new SqlServerPersistenceStartupGate(
            "Server=unused;Database=unused",
            new SqlPersistenceStartupOptions(timeout),
            (connectionString, actualTimeout, cancellationToken) =>
            {
                Assert.Equal("Server=unused;Database=unused", connectionString);
                Assert.Equal(timeout, actualTimeout);
                Assert.False(cancellationToken.IsCancellationRequested);
                order.Add("migration");
                return Task.CompletedTask;
            },
            (connectionString, actualTimeout, cancellationToken) =>
            {
                Assert.Equal("Server=unused;Database=unused", connectionString);
                Assert.Equal(timeout, actualTimeout);
                Assert.False(cancellationToken.IsCancellationRequested);
                order.Add("verification");
                return Task.FromResult(CreateCompatibleResult());
            });

        await gate.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(["migration", "verification"], order);
    }

    [Fact]
    public async Task MigrationFailureIsTranslatedAndVerificationIsNotInvoked()
    {
        var cause = new InvalidOperationException("migration failure");
        var verificationInvoked = false;
        var gate = CreateGate(
            (_, _, _) => Task.FromException(cause),
            (_, _, _) =>
            {
                verificationInvoked = true;
                return Task.FromResult(CreateCompatibleResult());
            });

        var failure = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(SqlPersistenceStartupFailureKind.MigrationOperationalFailure, failure.FailureKind);
        Assert.Same(cause, failure.InnerException);
        Assert.False(verificationInvoked);
    }

    [Fact]
    public async Task MigrationCancellationPropagatesUnchanged()
    {
        var cancellation = new OperationCanceledException("migration cancellation");
        var gate = CreateGate(
            (_, _, _) => Task.FromException(cancellation),
            (_, _, _) => Task.FromResult(CreateCompatibleResult()));

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Same(cancellation, actual);
    }

    [Fact]
    public async Task VerificationFailureIsTranslatedAfterMigrationSuccess()
    {
        var cause = new InvalidOperationException("verification failure");
        var gate = CreateGate(
            (_, _, _) => Task.CompletedTask,
            (_, _, _) => Task.FromException<SqlRuntimeCompatibilityResult>(cause));

        var failure = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(SqlPersistenceStartupFailureKind.VerificationOperationalFailure, failure.FailureKind);
        Assert.Same(cause, failure.InnerException);
    }

    [Fact]
    public async Task VerificationCancellationPropagatesUnchanged()
    {
        var cancellation = new OperationCanceledException("verification cancellation");
        var gate = CreateGate(
            (_, _, _) => Task.CompletedTask,
            (_, _, _) => Task.FromException<SqlRuntimeCompatibilityResult>(cancellation));

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Same(cancellation, actual);
    }

    [Fact]
    public async Task NonCompatibleVerificationResultIsPreservedExactly()
    {
        var incompatible = CreateIncompatibleResult();
        var gate = CreateGate(
            (_, _, _) => Task.CompletedTask,
            (_, _, _) => Task.FromResult(incompatible));

        var failure = await Assert.ThrowsAsync<SqlPersistenceStartupException>(
            async () => await gate.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(SqlPersistenceStartupFailureKind.DatabaseIncompatible, failure.FailureKind);
        Assert.Same(incompatible, failure.CompatibilityResult);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task SqlProviderBindsStartupTimeoutFromExistingProviderSection()
    {
        var configuration = BuildConfiguration(
            ("Persistence:Provider", "SqlServer"),
            ("PersistenceProviders:SqlServer:ConnectionString", "Server=unused;Database=unused"),
            ("PersistenceProviders:SqlServer:Startup:LockTimeout", "00:00:07"));
        var services = new ServiceCollection();
        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(configuration);

        await using var provider = services.BuildServiceProvider();
        var gate = Assert.IsType<SqlServerPersistenceStartupGate>(
            provider.GetRequiredService<IPersistenceStartupGate>());

        Assert.Equal(TimeSpan.FromSeconds(7), gate.LockTimeout);
    }

    private static SqlServerPersistenceStartupGate CreateGate(
        Func<string, TimeSpan, CancellationToken, Task> migration,
        Func<string, TimeSpan, CancellationToken, Task<SqlRuntimeCompatibilityResult>> verification) =>
        new(
            "Server=unused;Database=unused",
            new SqlPersistenceStartupOptions(TimeSpan.FromSeconds(1)),
            migration,
            verification);

    private static PersistenceProviderRegistration CreateRegistration(
        string key,
        IPersistenceStartupGate gate) =>
        new(
            key,
            PersistenceProviderCapabilities.Core,
            static _ => throw new InvalidOperationException("Store factory must not be invoked."),
            _ => gate);

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                static item => item.Key,
                static item => (string?)item.Value,
                StringComparer.Ordinal))
            .Build();

    private static SqlRuntimeCompatibilityResult CreateCompatibleResult() =>
        new(
            SqlRuntimeCompatibilityClassification.Compatible,
            ImmutableArray<SqlRuntimeCompatibilityDiagnostic>.Empty);

    private static SqlRuntimeCompatibilityResult CreateIncompatibleResult() =>
        new(
            SqlRuntimeCompatibilityClassification.MigrationPending,
            ImmutableArray.Create(
                new SqlRuntimeCompatibilityDiagnostic(
                    SqlRuntimeCompatibilityDiagnosticCode.MigrationPending,
                    SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship,
                    "Migration:004_ProductionContextMetricInputHandoff",
                    expected: "present",
                    actual: "missing",
                    detail: "Repository migration is pending.")));

    private sealed class ProbeStartupGate : IPersistenceStartupGate
    {
        public int InvocationCount { get; private set; }

        public ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.CompletedTask;
        }
    }
}
