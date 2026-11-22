import { useState } from "react";

type Profile = {
  profileRevisionId: string;
  revisionNumber: number;
  evidenceMode: string;
  stateDataItemId: string | null;
  counterDataItemId: string | null;
  counterSemantics: string | null;
  effectiveFromUtc: string;
};

export function CounterProfileInspection({ machineId, atUtc }: {
  readonly machineId: string;
  readonly atUtc: string;
}) {
  const [result, setResult] = useState<{ key: string; profile: Profile | null } | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const key = machineId + "|" + atUtc;
  const current = result?.key === key ? result.profile : null;

  async function inspect() {
    if (busy) return;
    setBusy(true); setResult(null); setMessage("");
    try {
      const url = `/api/commissioning/v1/machines/${encodeURIComponent(machineId)}/evidence-profile?atUtc=${encodeURIComponent(atUtc)}`;
      const response = await fetch(url, { cache: "no-store", redirect: "error" });
      if (response.status === 404) {
        setResult({ key, profile: null });
        setMessage("No machine evidence profile effective at this instant.");
        return;
      }
      if (!response.ok) throw new Error(`Profile inspection unavailable (HTTP ${response.status}).`);
      const value: unknown = await response.json();
      if (value === null || typeof value !== "object" ||
        typeof (value as Profile).profileRevisionId !== "string" ||
        typeof (value as Profile).revisionNumber !== "number" ||
        typeof (value as Profile).evidenceMode !== "string" ||
        typeof (value as Profile).effectiveFromUtc !== "string")
        throw new Error("Invalid profile response.");
      setResult({ key, profile: value as Profile });
      setMessage("Effective machine evidence configuration loaded.");
    } catch (error) {
      setMessage(String(error));
    } finally { setBusy(false); }
  }

  return <section aria-label="Effective machine evidence profile">
    <h3>Configured counter evidence — local inspection</h3>
    <p>Read the SQL-backed machine evidence profile effective at the selected interval start.
      This local commissioning read may be unavailable outside the factory host.
      Configuration alone does not establish a raw signal match, continuity, produced quantity or OEE.</p>
    <button type="button" disabled={busy} onClick={() => void inspect()}>
      {busy ? "Inspecting profile…" : "Inspect effective profile"}
    </button>
    {message && <p role="status">{message}</p>}
    {current && <dl>
      <dt>Revision</dt><dd>{current.revisionNumber}</dd>
      <dt>Evidence mode</dt><dd>{current.evidenceMode}</dd>
      <dt>Configured state data item ID</dt><dd>{current.stateDataItemId ?? "Not configured"}</dd>
      <dt>Configured counter data item ID</dt><dd>{current.counterDataItemId ?? "Not configured"}</dd>
      <dt>Counter semantics</dt><dd>{current.counterSemantics ?? "Not applicable"}</dd>
      <dt>Effective from UTC</dt><dd>{current.effectiveFromUtc}</dd>
    </dl>}
    {current && <p>Compare the configured MTConnect data item ID with the exact raw source/address
      independently. This screen does not assert that they refer to the same signal.</p>}
  </section>;
}
