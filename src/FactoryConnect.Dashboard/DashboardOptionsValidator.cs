using Microsoft.Extensions.Options;

namespace FactoryConnect.Dashboard;

public sealed class DashboardOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<DashboardOptions>
{
    private static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromMinutes(5);

    public ValidateOptionsResult Validate(string? name, DashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!Uri.TryCreate(options.ReportingApiBaseAddress, UriKind.Absolute, out var reportingApiUri) ||
            (reportingApiUri.Scheme != Uri.UriSchemeHttp && reportingApiUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Dashboard:ReportingApiBaseAddress must be an absolute HTTP or HTTPS URI.");
        }
        else if (environment.IsProduction() && reportingApiUri.IsLoopback)
        {
            failures.Add("Dashboard:ReportingApiBaseAddress must not use localhost or another loopback address in Production.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > MaximumRequestTimeout)
        {
            failures.Add("Dashboard:RequestTimeout must be greater than zero and no more than five minutes.");
        }

        if (options.Sources.Count == 0)
        {
            failures.Add("Dashboard:Sources must contain at least one reporting source.");
        }

        var identities = new HashSet<(Guid MachineId, string ProcessorId)>();
        for (var index = 0; index < options.Sources.Count; index++)
        {
            var source = options.Sources[index];
            if (source.MachineId == Guid.Empty)
            {
                failures.Add($"Dashboard:Sources:{index}:MachineId must be non-empty.");
            }

            if (string.IsNullOrWhiteSpace(source.ProcessorId))
            {
                failures.Add($"Dashboard:Sources:{index}:ProcessorId must be non-empty.");
            }
            else if (!string.Equals(source.ProcessorId, source.ProcessorId.Trim(), StringComparison.Ordinal))
            {
                failures.Add($"Dashboard:Sources:{index}:ProcessorId must not contain leading or trailing whitespace.");
            }

            if (string.IsNullOrWhiteSpace(source.DisplayName))
            {
                failures.Add($"Dashboard:Sources:{index}:DisplayName must be non-empty.");
            }

            if (source.MachineId != Guid.Empty && !string.IsNullOrWhiteSpace(source.ProcessorId) &&
                !identities.Add((source.MachineId, source.ProcessorId)))
            {
                failures.Add($"Dashboard:Sources contains duplicate source ({source.MachineId}, {source.ProcessorId}).");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
