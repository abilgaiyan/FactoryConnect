import { useEffect, useState } from "react";

type Diagnostic = {
  classification: string;
  machineId: string;
  entryId: string;
  currentEvidenceId: string | null;
  sourceKind: string | null;
  localReviewDecision: string | null;
  readinessStatus: string;
  publicationAuthorized: boolean;
  warning: string;
};

/** Explicit, on-demand SQL inspection; never infer approval from a local review. */
export function SupervisorEntryReadiness({ machineId, entryId }: {
  readonly machineId: string; readonly entryId: string;
}) {
  const [generation, setGeneration] = useState(0);
  const [state, setState] = useState<
    { kind: "idle" | "loading" | "error" } | { kind: "ready"; data: Diagnostic }
  >({ kind: "idle" });
  useEffect(() => {
    if (!generation) return;
    const controller = new AbortController();
    setState({ kind: "loading" });
    const url = `/api/supervisor/v1/machines/${encodeURIComponent(machineId)}/production-entries/${encodeURIComponent(entryId)}/qualification-readiness`;
    void (async () => {
      try {
        const response = await fetch(url, { signal: controller.signal });
        if (!response.ok) throw new Error("Readiness inspection unavailable");
        const value: unknown = await response.json();
        if (!value || typeof value !== "object") throw new Error("Invalid readiness response");
        const data = value as Partial<Diagnostic>;
        if (data.classification !== "SupervisorQualificationReadinessDiagnosticOnly" ||
            data.machineId !== machineId || data.entryId !== entryId ||
            typeof data.readinessStatus !== "string" || !data.readinessStatus ||
            data.publicationAuthorized !== false)
          throw new Error("Invalid readiness identity or authority");
        if (!controller.signal.aborted) setState({ kind: "ready", data: data as Diagnostic });
      } catch {
        if (!controller.signal.aborted) setState({ kind: "error" });
      }
    })();
    return () => controller.abort();
  }, [machineId, entryId, generation]);
  return <section aria-label="Supervisor entry qualification readiness">
    <button type="button" onClick={() => { setState({ kind: "loading" }); setGeneration(n => n + 1); }}>
      {generation ? "Refresh qualification blockers" : "Inspect qualification blockers"}
    </button>
    {state.kind === "loading" && <p role="status">Reading current SQL qualification status…</p>}
    {state.kind === "error" && <p role="alert">Qualification status unavailable. No approval or production publication can be inferred.</p>}
    {state.kind === "ready" && <>
      <p role="status">Qualification blocker: <strong>{state.data.readinessStatus}</strong></p>
      <p>Local review: {state.data.localReviewDecision ?? "Not recorded"} · Evidence: {state.data.sourceKind ?? "Not found"}</p>
      <p>Publication not authorized. This diagnostic is not verified production or OEE evidence.</p>
    </>}
  </section>;
}
