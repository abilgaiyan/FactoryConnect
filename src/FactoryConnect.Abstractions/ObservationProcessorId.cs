namespace FactoryConnect.Abstractions;

public sealed record ObservationProcessorId
{
    public ObservationProcessorId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
