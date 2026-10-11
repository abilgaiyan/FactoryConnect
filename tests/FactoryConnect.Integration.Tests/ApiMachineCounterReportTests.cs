using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryConnect.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FactoryConnect.Integration.Tests;

public sealed class ApiMachineCounterReportTests
{
    private static readonly MachineId Machine = new(Guid.Parse("de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed"));
    private static readonly ObservationStreamId Stream = new(Machine, "mtconnect:M01");
    private static readonly CurrentStateAuthorityBinding Binding = new(Machine, Stream, new("canonical-mapping"), new("machine-state-activity"));
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Path = $"/api/machines/v1/{Machine}/counter/query";
    private static object Request => new { fromInclusive = Start, toExclusive = Start.AddHours(1), source = "mtconnect", address = "part_count" };
    private static DurableMachineObservation Sample(ulong position) => new(new(position), Stream, 1, position,
        new MachineObservation { MachineId = Machine, Source = "mtconnect", Address = "part_count", Type = SignalType.Numeric, Value = (decimal)position, Timestamp = Start.AddTicks((long)position) });
    private static WebApplicationFactory<Program> Factory(ICurrentStateAuthorityCutProvider cuts, IDurableObservationReader reader) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICurrentStateOwnerResolver>(); services.AddSingleton<ICurrentStateOwnerResolver>(new Owner());
            services.RemoveAll<ICurrentStateAuthorityCutProvider>(); services.AddSingleton(cuts);
            services.RemoveAll<IDurableObservationReader>(); services.AddSingleton(reader);
        }));

    [Fact]
    public async Task RawAcquisitionBoundaryExcludesLaterSamplesWithoutStateEvaluation()
    {
        using var factory = Factory(new Cut(new(2)), new Reader([Sample(1), Sample(2), Sample(3)]));
        using var client = factory.CreateClient(); using var response = await client.PostAsJsonAsync(Path, Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2", json.GetProperty("throughPosition").GetString());
        Assert.Equal(2, json.GetProperty("samples").GetArrayLength());
        Assert.Equal("1", json.GetProperty("samples")[1].GetProperty("arithmeticDifference").GetString());
    }

    [Fact]
    public async Task MissingRawBoundaryIsUnavailableAndDoesNotReadHistory()
    {
        var reader = new Reader([]); using var factory = Factory(new Cut(null), reader);
        using var client = factory.CreateClient(); using var response = await client.PostAsJsonAsync(Path, Request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task LaterPageFailureCannotReturnPartialCounterReport()
    {
        using var factory = Factory(new Cut(new(1001)), new Reader(Enumerable.Range(1, 1001).Select(i => Sample((ulong)i)).ToArray(), true));
        using var client = factory.CreateClient(); using var response = await client.PostAsJsonAsync(Path, Request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("arithmeticDifference", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PrematureHistoryEndIsIntegrityFailureRatherThanAbsence()
    {
        using var factory = Factory(new Cut(new(2)), new Reader([Sample(1)]));
        using var client = factory.CreateClient(); using var response = await client.PostAsJsonAsync(Path, Request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task InvalidIntervalIsRejectedBeforeRetrieval()
    {
        var reader = new Reader([]); using var factory = Factory(new Cut(new(2)), reader);
        using var client = factory.CreateClient(); using var response = await client.PostAsJsonAsync(Path,
            new { fromInclusive = Start, toExclusive = Start.AddDays(32), source = "mtconnect", address = "part_count" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, reader.ReadCount);
    }

    private sealed class Owner : ICurrentStateOwnerResolver
    {
        public CurrentStateOwnerResolution Resolve(MachineId machineId) => new CurrentStateExactlyOneOwner(Binding);
    }
    private sealed class Cut(ObservationPosition? through) : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(CurrentStateAuthorityBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult<CurrentStateAuthorityCutReadResult>(new StableCurrentStateAuthorityCut(new(binding,
                through is null ? null : new AcquisitionContactAuthority(Stream, Start, through, new(2)), null, null)));
    }
    private sealed class Reader(IReadOnlyList<DurableMachineObservation> observations, bool failLater = false) : IDurableObservationReader
    {
        public int ReadCount { get; private set; }
        public ValueTask<ObservationReadBatch> ReadAsync(ObservationReadRequest request, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (failLater && request.AfterPosition is not null) throw new IOException("Injected second-page failure.");
            var remaining = observations.Where(o => o.Position > request.AfterPosition).ToArray();
            return ValueTask.FromResult(new ObservationReadBatch(request.StreamId, remaining.Take(request.BatchSize).ToArray(), remaining.Length > request.BatchSize));
        }
    }
}
