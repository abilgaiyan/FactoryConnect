using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryConnect.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FactoryConnect.Integration.Tests;

public sealed class ApiMachineActivityReportTests
{
    private static readonly MachineId Machine = new(Guid.Parse("de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed"));
    private static readonly ObservationStreamId Stream = new(Machine, "activity");
    private static readonly ObservationProcessorId Processor = new("machine-state-activity");
    private static readonly CurrentStateAuthorityBinding Binding = new(Machine, Stream, new("canonical-mapping"), Processor);
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Path = $"/api/machines/v1/{Machine}/activity/query";
    private static object Request => new { fromInclusive = Start, toExclusive = Start.AddHours(1) };

    private static WebApplicationFactory<Program> Factory(ICurrentStateAuthorityCutProvider cuts, IProductionContextActivityReader reader) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICurrentStateOwnerResolver>(); services.AddSingleton<ICurrentStateOwnerResolver>(new Owner());
            services.RemoveAll<ICurrentStateAuthorityCutProvider>(); services.AddSingleton(cuts);
            services.RemoveAll<IProductionContextActivityReader>(); services.AddSingleton(reader);
        }));

    [Fact]
    public async Task NoEvaluationReturnsNoStoredTimeAndDoesNotReadHistory()
    {
        var reader = new Reader([]);
        using var factory = Factory(new Cut(null), reader); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(Path, Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("intervals").GetArrayLength());
        Assert.Equal(TimeSpan.FromHours(1).Ticks, json.GetProperty("unrepresentedTicks").GetInt64());
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task ExcludesActivityBeyondPinnedStateCheckpoint()
    {
        var reader = new Reader([
            new(Processor, new(1), Stream, 1, 1, new(Machine, MachineState.Running, Start, Start.AddMinutes(10))),
            new(Processor, new(11), Stream, 1, 11, new(Machine, MachineState.Stopped, Start.AddMinutes(10), Start.AddMinutes(20)))]);
        using var factory = Factory(new Cut(new(10)), reader); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(Path, Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("10", json.GetProperty("throughPosition").GetString());
        Assert.Equal(1, json.GetProperty("intervals").GetArrayLength());
        Assert.Equal("running", json.GetProperty("intervals")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task UnavailableCutCannotProduceAnEmptyReport()
    {
        using var factory = Factory(new UnavailableCut(), new Reader([])); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(Path, Request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task InvalidUtcIntervalIsRejectedBeforeActivityRead()
    {
        var reader = new Reader([]);
        using var factory = Factory(new Cut(new(10)), reader); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(Path,
            new { fromInclusive = Start.ToOffset(TimeSpan.FromHours(5.5)), toExclusive = Start.AddHours(1) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, reader.ReadCount);
    }

    private sealed class Owner : ICurrentStateOwnerResolver
    {
        public CurrentStateOwnerResolution Resolve(MachineId machineId) => new CurrentStateExactlyOneOwner(Binding);
    }
    private sealed class Cut(ObservationPosition? through) : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(CurrentStateAuthorityBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult<CurrentStateAuthorityCutReadResult>(new StableCurrentStateAuthorityCut(new(binding, null, null,
                through is null ? null : new EvaluationAuthority(Processor, Stream, through, MachineState.Running, 1,
                    new("test", "1"), new(1)))));
    }
    private sealed class UnavailableCut : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(CurrentStateAuthorityBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult<CurrentStateAuthorityCutReadResult>(StableCurrentStateAuthorityCutUnavailable.Instance);
    }
    private sealed class Reader(IReadOnlyList<DurableMachineActivityPeriod> periods) : IProductionContextActivityReader
    {
        public int ReadCount { get; private set; }
        public Task<IReadOnlyList<DurableMachineActivityPeriod>> ReadAsync(ObservationStreamId streamId, ObservationPosition? afterPosition, int batchSize, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult<IReadOnlyList<DurableMachineActivityPeriod>>(periods.Where(p => p.Position > afterPosition).Take(batchSize).ToArray());
        }
    }
}
