using Xunit;

namespace FactoryConnect.Integration.Tests;

[CollectionDefinition(
    CollectionName,
    DisableParallelization = true)]
public sealed class SqlStartupConcurrencyCollection
{
    public const string CollectionName = "FC-030 E5 SQL concurrency";
}
