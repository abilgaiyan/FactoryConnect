import { useState, type FormEvent } from "react";

interface ShiftScope {
  readonly machineId: string;
  readonly shiftScheduleAssignmentId: string;
  readonly shiftId: string;
  readonly startsAtUtc: string;
  readonly endsAtUtc: string;
}
interface PreviewResponse {
  readonly status: string;
  readonly authority: string;
  readonly preview: {
    readonly observedUnitIncrements: number;
    readonly boundaryAmbiguousIncrements: number;
    readonly gapFlaggedIncrements: number;
    readonly nonUnitTransitions: number;
    readonly usableReadings: number;
    readonly hasEvidence: boolean;
  };
}
export function CncShiftEvidencePreview({ scope }: { readonly scope: ShiftScope }) {
  const [result, setResult] = useState<PreviewResponse | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  async function load(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setResult(null); setError(""); setLoading(true);
    try {
      const response = await fetch("/api/supervisor/cnc-shift-evidence-preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          machineId: scope.machineId,
          assignmentId: scope.shiftScheduleAssignmentId,
          shiftId: scope.shiftId,
          startsAtUtc: scope.startsAtUtc,
          endsAtUtc: scope.endsAtUtc,
        }),
      });
      if (!response.ok) throw new Error(response.status === 404
        ? "CNC preview is not enabled on this installation."
        : response.status === 422 ? "CNC evidence unavailable for this shift. Ask an administrator to check counter configuration and evidence coverage." : `CNC evidence unavailable (HTTP ${response.status}).`);
      const payload = await response.json() as PreviewResponse;
      setResult(payload);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "CNC preview unavailable.");
    } finally {
      setLoading(false);
    }
  }
  return <section aria-label="Provisional CNC shift evidence" className="supervisor-detail">
    <h2>CNC shift evidence (provisional)</h2>
    <p>Uses the configured counter and current acquisition stream as diagnostic candidates. These counts are not accepted production and do not feed OEE.</p>
    <form onSubmit={event => { void load(event); }}>
      <button type="submit" disabled={loading}>{loading ? "Loading…" : "Inspect CNC evidence"}</button>
    </form>
    {error && <p role="alert">{error}</p>}
    {result && <div role="status">
      <p><strong>{result.preview.hasEvidence ? "Provisional observations" : "No evidence"}</strong> — not a verified zero or accepted quantity.</p>
      {result.preview.hasEvidence && <dl>
        <dt>Observed +1 transitions</dt><dd>{result.preview.observedUnitIncrements}</dd>
        <dt>Boundary ambiguous</dt><dd>{result.preview.boundaryAmbiguousIncrements}</dd>
        <dt>Gap flagged</dt><dd>{result.preview.gapFlaggedIncrements}</dd>
        <dt>Non-unit transitions</dt><dd>{result.preview.nonUnitTransitions}</dd>
        <dt>Usable readings</dt><dd>{result.preview.usableReadings}</dd>
      </dl>}
    </div>}
  </section>;
}
