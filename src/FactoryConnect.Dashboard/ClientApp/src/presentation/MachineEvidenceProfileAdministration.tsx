import { type FormEvent, useState } from "react";

type Mode = "StateOnly" | "CounterOnly" | "CncCounterAndState";
type Semantics = "CumulativeResettable" | "CumulativeMonotonic" | "CyclePulse";
type Profile = {
  profileRevisionId: string; machineId: string; revisionNumber: number;
  evidenceMode: Mode; stateDataItemId: string | null; counterDataItemId: string | null;
  counterSemantics: Semantics | null; effectiveFromUtc: string;
  configuredBy: string; configuredAtUtc: string;
};
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const apiPath = (machine: string) => `/api/commissioning/v1/machines/${encodeURIComponent(machine)}/evidence-profiles`;
const hasState = (mode: Mode) => mode !== "CounterOnly";
const hasCounter = (mode: Mode) => mode !== "StateOnly";

export function MachineEvidenceProfileAdministration() {
  const [machine, setMachine] = useState("");
  const [selectedMachine, setSelectedMachine] = useState("");
  const [history, setHistory] = useState<Profile[] | null>(null);
  const [mode, setMode] = useState<Mode>("StateOnly");
  const [stateId, setStateId] = useState("");
  const [counterId, setCounterId] = useState("");
  const [semantics, setSemantics] = useState<Semantics>("CumulativeResettable");
  const [effective, setEffective] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const head = history?.at(-1);
  const validMachine = uuid.test(machine) && machine !== "00000000-0000-0000-0000-000000000000";

  async function load(event?: FormEvent) {
    event?.preventDefault();
    if (!validMachine || busy) return;
    setBusy(true); setHistory(null); setSelectedMachine(""); setMessage("");
    try {
      const response = await fetch(apiPath(machine), { cache: "no-store", redirect: "error" });
      if (!response.ok) throw new Error(`Machine profile history unavailable (HTTP ${response.status}).`);
      const data: unknown = await response.json();
      if (!Array.isArray(data) || !data.every(item =>
        item !== null && typeof item === "object" && typeof item.profileRevisionId === "string" &&
        typeof item.revisionNumber === "number" && typeof item.effectiveFromUtc === "string"))
        throw new Error("Invalid machine profile history.");
      const revisions = data as Profile[];
      setHistory(revisions); setSelectedMachine(machine);
      setMessage(revisions.length ? "Revision history loaded." : "No profile configured for this machine.");
    } catch (error) { setMessage(String(error)); }
    finally { setBusy(false); }
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    if (busy || selectedMachine !== machine || history === null || !validMachine) return;
    if ((hasState(mode) && !stateId.trim()) || (hasCounter(mode) && !counterId.trim()) || !effective) return;
    const effectiveFromUtc = new Date(effective).toISOString();
    if (head && Date.parse(effectiveFromUtc) <= Date.parse(head.effectiveFromUtc)) {
      setMessage("Effective time must be later than the latest revision."); return;
    }
    setBusy(true); setMessage("");
    try {
      const response = await fetch(apiPath(machine), {
        method: "POST", redirect: "error",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          profileRevisionId: crypto.randomUUID(), revisionNumber: (head?.revisionNumber ?? 0) + 1,
          expectedHeadRevisionId: head?.profileRevisionId ?? null, evidenceMode: mode,
          stateDataItemId: hasState(mode) ? stateId.trim() : null,
          counterDataItemId: hasCounter(mode) ? counterId.trim() : null,
          counterSemantics: hasCounter(mode) ? semantics : null, effectiveFromUtc
        })
      });
      if (!response.ok) throw new Error(`Profile not confirmed saved (HTTP ${response.status}). Refresh history before retrying.`);
      // Read after write: never assume the displayed head changed without a successful read.
      const refreshed = await fetch(apiPath(machine), { cache: "no-store", redirect: "error" });
      if (!refreshed.ok) throw new Error("Save accepted but history refresh failed; refresh before editing.");
      const data: unknown = await refreshed.json();
      if (!Array.isArray(data)) throw new Error("Save accepted but history response was invalid.");
      setHistory(data as Profile[]);
      setMessage("Machine evidence profile saved. This does not activate acquisition or qualify production quantity.");
    } catch (error) { setHistory(null); setSelectedMachine(""); setMessage(String(error)); }
    finally { setBusy(false); }
  }

  return <section aria-label="Machine evidence profile administration">
    <h2>Machine evidence configuration</h2>
    <p>Local commissioning only. Enter the commissioned machine UUID and verified MTConnect data item IDs. A profile does not activate acquisition or establish produced quantity.</p>
    <form onSubmit={event => void load(event)}>
      <label>Machine UUID <input value={machine} onChange={event => { setMachine(event.target.value.trim()); setHistory(null); setSelectedMachine(""); }} placeholder="Commissioned machine UUID" required /></label>
      <button type="submit" disabled={!validMachine || busy}>Load machine profiles</button>
    </form>
    {message && <p role="status">{message}</p>}
    {history !== null && selectedMachine === machine && <>
      <h3>Revision history</h3>
      {history.length === 0 ? <p>Unconfigured — no default evidence mode is assumed.</p> :
        <table><thead><tr><th>Revision</th><th>Mode</th><th>State signal</th><th>Counter signal</th><th>Effective UTC</th></tr></thead>
          <tbody>{history.map(item => <tr key={item.profileRevisionId}><td>{item.revisionNumber}</td>
            <td>{item.evidenceMode}</td><td>{item.stateDataItemId ?? "—"}</td>
            <td>{item.counterDataItemId ?? "—"}</td><td>{item.effectiveFromUtc}</td></tr>)}</tbody></table>}
      <form onSubmit={event => void save(event)}>
        <fieldset disabled={busy}>
          <legend>Append revision {(head?.revisionNumber ?? 0) + 1}</legend>
          <label>Evidence mode <select value={mode} onChange={event => setMode(event.target.value as Mode)}>
            <option value="StateOnly">State only</option>
            <option value="CncCounterAndState">CNC counter and state</option>
            <option value="CounterOnly">Counter only</option>
          </select></label>
          {hasState(mode) && <label>MTConnect state data item ID <input required maxLength={200} value={stateId} onChange={event => setStateId(event.target.value)} /></label>}
          {hasCounter(mode) && <>
            <label>MTConnect counter data item ID <input required maxLength={200} value={counterId} onChange={event => setCounterId(event.target.value)} /></label>
            <label>Counter semantics <select value={semantics} onChange={event => setSemantics(event.target.value as Semantics)}>
              <option value="CumulativeResettable">Cumulative resettable</option>
              <option value="CumulativeMonotonic">Cumulative monotonic</option>
              <option value="CyclePulse">Cycle pulse</option>
            </select></label>
          </>}
          <label>Effective from (local time; stored as UTC) <input type="datetime-local" required value={effective} onChange={event => setEffective(event.target.value)} /></label>
          <button type="submit">Save new revision</button>
        </fieldset>
      </form>
    </>}
  </section>;
}
