using System.Text.Json.Serialization;

namespace FactoryConnect.Api.Machines;

public sealed record CurrentMachineStateResponse
{
    public CurrentMachineStateResponse(
        Guid machineId,
        string outcome,
        string coverage,
        CurrentMachineStateEvidenceResponse? evidence)
    {
        MachineId = machineId;
        Outcome = outcome;
        Coverage = coverage;
        Evidence = evidence;
    }

    [JsonPropertyName("machineId")]
    public Guid MachineId { get; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; }

    [JsonPropertyName("coverage")]
    public string Coverage { get; }

    [JsonPropertyName("evidence")]
    public CurrentMachineStateEvidenceResponse? Evidence { get; }
}

public sealed record CurrentMachineStateEvidenceResponse
{
    public CurrentMachineStateEvidenceResponse(
        string machineState,
        string freshness,
        string usability,
        DateTimeOffset readAsOf)
    {
        MachineState = machineState;
        Freshness = freshness;
        Usability = usability;
        ReadAsOf = readAsOf;
    }

    [JsonPropertyName("machineState")]
    public string MachineState { get; }

    [JsonPropertyName("freshness")]
    public string Freshness { get; }

    [JsonPropertyName("usability")]
    public string Usability { get; }

    [JsonPropertyName("readAsOf")]
    public DateTimeOffset ReadAsOf { get; }
}
