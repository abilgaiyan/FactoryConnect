using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.AssessmentAuthority;

public sealed class AssessmentAuthorityInputResolver : IAssessmentAuthorityInputResolver
{
    private readonly IAssessmentAuthorityExactReader _reader;
    public AssessmentAuthorityInputResolver(IAssessmentAuthorityExactReader reader)
    { ArgumentNullException.ThrowIfNull(reader); _reader = reader; }

    public async ValueTask<AuthorityInputResolution> ResolveExactAsync(AuthorityVerificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        var invalid = AuthoritySnapshotValidation.ValidateRequest(request);
        if (invalid is not null) { return new AuthorityInputResolution.Failed(request, invalid); }
        var lookups = new List<AuthorityExactLookup>();
        foreach (var reference in request.Selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Reader adapters translate recognized retrieval failures; cancellation and unexpected exceptions propagate.
            var lookup = await _reader.ReadExactAsync(reference, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (lookup is null || lookup.RequestedReference != reference)
            { return new AuthorityInputResolution.Failed(request, new(AuthorityOperationErrorKind.ReferenceMismatch, AuthorityDiagnosticCode.LookupReferenceMismatch, reference)); }
            if (lookup is AuthorityExactLookup.Failed failed) { return new AuthorityInputResolution.Failed(request, failed.Error); }
            if (lookup is AuthorityExactLookup.Unavailable unavailable)
            { return new AuthorityInputResolution.Unavailable(request, reference, unavailable.Reason); }
            if (lookup is AuthorityExactLookup.Found found && found.Value.Reference != reference)
            { return new AuthorityInputResolution.Failed(request, new(AuthorityOperationErrorKind.ReferenceMismatch, AuthorityDiagnosticCode.ValueReferenceMismatch, reference)); }
            lookups.Add(lookup);
        }
        var inputs = new ResolvedAuthorityVerificationInputs(request, lookups);
        var error = AuthoritySnapshotValidation.Validate(inputs, cancellationToken);
        return error is null ? new AuthorityInputResolution.Resolved(inputs) : new AuthorityInputResolution.Failed(request, error);
    }
}
