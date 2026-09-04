using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerRuntimeCompatibilityMatrixContractTests
{
    [Fact]
    public void RealSqlScenarioVocabularyMatchesFinalCompatibilityVocabularyExactly()
    {
        Assert.Equal(
            Enum.GetNames<SqlRuntimeCompatibilityClassification>(),
            Enum.GetNames<SqlServerRuntimeCompatibilityMatrixIntegrationTests.D5Scenario>());
    }
}
