namespace FactoryConnect.Abstractions;

public interface IMetricInputAppender
{
    ValueTask<PositionedMetricInputFact> AppendAsync(
        DurableMetricInputAppend append,
        CancellationToken cancellationToken);
}
