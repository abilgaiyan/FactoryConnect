namespace FactoryConnect.Abstractions;

public abstract record AssessmentAuthorityScope
{
    private protected AssessmentAuthorityScope(CompanyId company, SiteId site)
    { if (company.IsEmpty || site.IsEmpty) { throw new ArgumentException("Explicit company and site are required.", nameof(company)); } Company = company; Site = site; }
    public CompanyId Company { get; }
    public SiteId Site { get; }
    public sealed record SiteScope : AssessmentAuthorityScope
    { public SiteScope(CompanyId company, SiteId site) : base(company, site) { } }
    public sealed record LineScope : AssessmentAuthorityScope
    {
        public LineScope(CompanyId company, SiteId site, ProductionLineId line) : base(company, site)
        { if (line.IsEmpty) { throw new ArgumentException("Explicit line is required.", nameof(line)); } Line = line; }
        public ProductionLineId Line { get; }
    }
    public sealed record MachineScope : AssessmentAuthorityScope
    {
        public MachineScope(CompanyId company, SiteId site, ProductionLineId line, MachineId machine) : base(company, site)
        { if (line.IsEmpty) { throw new ArgumentException("Explicit line is required.", nameof(line)); } if (machine.IsEmpty) { throw new ArgumentException("Explicit machine is required.", nameof(machine)); } Line = line; Machine = machine; }
        public ProductionLineId Line { get; }
        public MachineId Machine { get; }
    }
    public bool Contains(AssessmentAuthorityScope other) => Company == other.Company && Site == other.Site && this switch
    {
        SiteScope => true,
        LineScope line => other is LineScope child && line.Line == child.Line
            || other is MachineScope machine && line.Line == machine.Line,
        MachineScope machine => other is MachineScope child && machine.Line == child.Line && machine.Machine == child.Machine,
        _ => false,
    };
}

public enum AssessmentAuthorityClaimKind
{
    ScheduleCompleteness = 1, FragmentAccounting, Gap, Completion,
    RevocationCompleteness, ProspectiveRevocation, RetrospectiveRevocation,
}

public sealed record AssessmentAuthorityGrant
{
    public AssessmentAuthorityGrant(AssessmentAuthorityClaimKind kind, AssessmentAuthorityScope scope,
        OperationalMetricCoverageInterval period, bool historicalIssuance, bool mayDelegate)
    {
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(period);
        Kind = kind; Scope = scope; Period = period; HistoricalIssuance = historicalIssuance; MayDelegate = mayDelegate;
    }
    public AssessmentAuthorityClaimKind Kind { get; }
    public AssessmentAuthorityScope Scope { get; }
    public OperationalMetricCoverageInterval Period { get; }
    public bool HistoricalIssuance { get; }
    public bool MayDelegate { get; }
}
