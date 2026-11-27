import { useEffect, useState } from "react";

type Part = { partNumber: string; displayName: string; idealCycleSeconds: number; isActive: boolean };
type Operation = { partNumber: string; operationCode: string; displayName: string; idealCycleSeconds: number | null; isActive: boolean };
const empty = { operationCode: "", displayName: "", idealCycleSeconds: "", isActive: true };
const size = 25;
const endpoint = "/api/admin/v1/operations";

async function readPage<T>(url: string, signal: AbortSignal, valid: (value: unknown) => value is T): Promise<T[]> {
  const response = await fetch(url, { signal, cache: "no-store", redirect: "error" });
  if (!response.ok) throw new Error("Master unavailable (HTTP " + response.status + ")");
  const data: unknown = await response.json();
  if (!Array.isArray(data) || !data.every(valid)) throw new Error("Invalid master response");
  return data;
}
function isPart(value: unknown): value is Part {
  if (!value || typeof value !== "object") return false;
  const p = value as Partial<Part>;
  return typeof p.partNumber === "string" && typeof p.displayName === "string" &&
    typeof p.idealCycleSeconds === "number" && typeof p.isActive === "boolean";
}
function isOperation(value: unknown): value is Operation {
  if (!value || typeof value !== "object") return false;
  const p = value as Partial<Operation>;
  return typeof p.partNumber === "string" && typeof p.operationCode === "string" &&
    typeof p.displayName === "string" && (p.idealCycleSeconds === null || typeof p.idealCycleSeconds === "number") &&
    typeof p.isActive === "boolean";
}
export function OperationMaster() {
  const [parts, setParts] = useState<Part[]>([]);
  const [partOffset, setPartOffset] = useState(0);
  const [part, setPart] = useState<Part | null>(null);
  const [rows, setRows] = useState<Operation[]>([]);
  const [offset, setOffset] = useState(0);
  const [refresh, setRefresh] = useState(0);
  const [form, setForm] = useState(empty);
  const [editing, setEditing] = useState(false);
  const [loadingParts, setLoadingParts] = useState(true);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [partError, setPartError] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    setLoadingParts(true); setPartError(""); setParts([]);
    readPage("/api/admin/v1/parts?offset=" + partOffset + "&limit=" + size, controller.signal, isPart)
      .then(data => { if (!controller.signal.aborted) setParts(data); })
      .catch((e: unknown) => { if (!controller.signal.aborted) setPartError(e instanceof Error ? e.message : "Parts unavailable"); })
      .finally(() => { if (!controller.signal.aborted) setLoadingParts(false); });
    return () => controller.abort();
  }, [partOffset, refresh]);
  const partNumber = part?.partNumber ?? "";
  useEffect(() => {
    if (!partNumber) return;
    const controller = new AbortController();
    setLoading(true); setRows([]); setError("");
    readPage(endpoint + "?partNumber=" + encodeURIComponent(partNumber) + "&offset=" + offset + "&limit=" + size, controller.signal, isOperation)
      .then(data => {
        if (data.some(o => o.partNumber !== partNumber)) throw new Error("Operation scope mismatch");
        if (!controller.signal.aborted) setRows(data);
      })
      .catch((e: unknown) => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : "Operations unavailable"); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [partNumber, offset, refresh]);
  function selectPart(value: string) {
    setPart(parts.find(p => p.partNumber === value) ?? null);
    setRows([]); setOffset(0); setForm(empty); setEditing(false); setError(""); setNotice("");
  }
  async function save() {
    if (!part) return;
    const cycle = form.idealCycleSeconds === "" ? null : Number(form.idealCycleSeconds);
    if (!form.operationCode.trim() || !form.displayName.trim() ||
      (cycle !== null && (!/^\d+(\.\d{1,3})?$/.test(form.idealCycleSeconds) || cycle <= 0 || cycle > 86400))) {
      setError("Enter an operation code, name and optional valid cycle time."); return;
    }
    setSaving(true); setError(""); setNotice("");
    try {
      const response = await fetch(endpoint, { method: "PUT", redirect: "error", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...form, partNumber, operationCode: form.operationCode.trim(), displayName: form.displayName.trim(), idealCycleSeconds: cycle }) });
      if (!response.ok) throw new Error("Save failed (HTTP " + response.status + ")");
      setForm(empty); setEditing(false); setNotice("Operation saved."); setRefresh(v => v + 1);
    } catch (e: unknown) { setError(e instanceof Error ? e.message : "Save failed"); }
    finally { setSaving(false); }
  }
  const selected = parts.find(p => p.partNumber === partNumber) ?? part;
  return <section className="supervisor-editor" aria-label="Operation master">
    <h2>Operation Master</h2>
    <p>Leave cycle time blank to inherit the part default. Changes do not revise historical production.</p>
    {partError && <p role="alert">{partError}</p>}
    <label>Part<select disabled={saving || loadingParts} value={partNumber} onChange={e => selectPart(e.target.value)}>
      <option value="">Select part</option>
      {part && !parts.some(p => p.partNumber === partNumber) && <option value={partNumber}>{partNumber} — {part.displayName}</option>}
      {parts.map(p => <option key={p.partNumber} value={p.partNumber}>{p.partNumber} — {p.displayName}{p.isActive ? "" : " (inactive)"}</option>)}
    </select></label>
    <button type="button" disabled={saving || loadingParts || partOffset === 0} onClick={() => setPartOffset(v => Math.max(0, v - size))}>Previous parts</button>
    <span> Part page {Math.floor(partOffset / size) + 1} </span>
    <button type="button" disabled={saving || loadingParts || parts.length < size} onClick={() => setPartOffset(v => v + size)}>Next parts</button>
    <button type="button" disabled={saving || loadingParts || loading} onClick={() => setRefresh(v => v + 1)}>Refresh masters</button>
    {selected && <>
      <p>Part default: {selected.idealCycleSeconds} seconds{selected.isActive ? "" : " (inactive part)"}</p>
      {error && <p role="alert">{error}</p>}
      {notice && <p role="status">{notice}</p>}
      <form onSubmit={e => { e.preventDefault(); if (!saving) void save(); }}>
        <fieldset disabled={saving}>
          <legend>{editing ? "Edit operation" : "Create or update operation"}</legend>
          <label>Operation code<input required maxLength={200} readOnly={editing} value={form.operationCode} onChange={e => setForm({ ...form, operationCode: e.target.value })} /></label>
          <label>Operation name<input required maxLength={200} value={form.displayName} onChange={e => setForm({ ...form, displayName: e.target.value })} /></label>
          <label>Cycle override (seconds)<input type="number" min="0.001" max="86400" step="0.001" value={form.idealCycleSeconds} onChange={e => setForm({ ...form, idealCycleSeconds: e.target.value })} /></label>
          <label><input type="checkbox" checked={form.isActive} onChange={e => setForm({ ...form, isActive: e.target.checked })} />Active</label>
          <button type="submit">{saving ? "Saving…" : "Save operation"}</button>
          <button type="button" onClick={() => { setForm(empty); setEditing(false); }}>Clear</button>
        </fieldset>
      </form>
      {loading ? <p role="status">Loading operations…</p> : <div style={{ overflowX: "auto" }}><table><thead><tr><th>Code</th><th>Name</th><th>Override (s)</th><th>Effective (s)</th><th>Status</th><th>Action</th></tr></thead>
        <tbody>{rows.map(o => <tr key={o.operationCode}><td>{o.operationCode}</td><td>{o.displayName}</td><td>{o.idealCycleSeconds ?? "Part default"}</td><td>{o.idealCycleSeconds ?? selected.idealCycleSeconds}</td><td>{o.isActive ? "Active" : "Inactive"}</td><td><button type="button" disabled={saving} onClick={() => { setForm({ operationCode: o.operationCode, displayName: o.displayName, idealCycleSeconds: o.idealCycleSeconds === null ? "" : String(o.idealCycleSeconds), isActive: o.isActive }); setEditing(true); }}>Edit</button></td></tr>)}</tbody></table>
        {rows.length === 0 && <p>No operations on this page.</p>}</div>}
      <button type="button" disabled={saving || loading || offset === 0} onClick={() => setOffset(v => Math.max(0, v - size))}>Previous operations</button>
      <span> Page {Math.floor(offset / size) + 1} </span>
      <button type="button" disabled={saving || loading || rows.length < size} onClick={() => setOffset(v => v + size)}>Next operations</button>
    </>}
  </section>;
}
