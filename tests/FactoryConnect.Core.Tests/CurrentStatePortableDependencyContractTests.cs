using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class CurrentStatePortableDependencyContractTests
{
    [Fact]
    public void OwnerResolverUsesMachineIdAndExistingOwnerResolutionAlgebra()
    {
        var method = Assert.Single(typeof(ICurrentStateOwnerResolver).GetMethods());
        var parameter = Assert.Single(method.GetParameters());

        Assert.Equal("Resolve", method.Name);
        Assert.Equal(typeof(MachineId), parameter.ParameterType);
        Assert.Equal(typeof(CurrentStateOwnerResolution), method.ReturnType);
    }

    [Fact]
    public void PolicyResolverUsesExistingGenericPolicyResolutionAlgebra()
    {
        var resolverType = typeof(ICurrentStatePolicyResolver<CurrentStateContinuityPolicy>);
        var method = Assert.Single(resolverType.GetMethods());

        Assert.Equal("Resolve", method.Name);
        Assert.Empty(method.GetParameters());
        Assert.Equal(
            typeof(CurrentStatePolicyResolution<CurrentStateContinuityPolicy>),
            method.ReturnType);
    }

    [Fact]
    public void FreshnessPolicyCarriesIdentityAndMaximumCurrentAgeOnly()
    {
        var properties = typeof(ICurrentStateFreshnessPolicy)
            .GetProperties()
            .OrderBy(property => property.Name)
            .ToArray();

        Assert.Equal(2, properties.Length);
        Assert.Equal("MaximumCurrentAge", properties[0].Name);
        Assert.Equal(typeof(TimeSpan), properties[0].PropertyType);
        Assert.Equal("Reference", properties[1].Name);
        Assert.Equal(typeof(CurrentStatePolicyReference), properties[1].PropertyType);
        Assert.DoesNotContain(
            typeof(ICurrentStateFreshnessPolicy).GetMethods(),
            method => !method.IsSpecialName);
    }

    [Fact]
    public void TimeProviderExposesOneDateTimeOffsetObservation()
    {
        var method = Assert.Single(typeof(ICurrentStateTimeProvider).GetMethods());

        Assert.Equal("GetUtcNow", method.Name);
        Assert.Empty(method.GetParameters());
        Assert.Equal(typeof(DateTimeOffset), method.ReturnType);
    }

    [Fact]
    public void PortableDependencyOperationsDoNotIntroduceCancellationOrServiceLocation()
    {
        var dependencyTypes = new[]
        {
            typeof(ICurrentStateOwnerResolver),
            typeof(ICurrentStatePolicyResolver<CurrentStateContinuityPolicy>),
            typeof(ICurrentStateTimeProvider),
        };

        foreach (var dependencyType in dependencyTypes)
        {
            Assert.DoesNotContain(
                dependencyType.GetMethods().SelectMany(method => method.GetParameters()),
                parameter => parameter.ParameterType == typeof(CancellationToken));

            Assert.DoesNotContain(
                dependencyType.GetMethods().SelectMany(method => method.GetParameters()),
                parameter => parameter.ParameterType == typeof(IServiceProvider));
        }
    }
}
