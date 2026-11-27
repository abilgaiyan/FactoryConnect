import { useEffect, useState } from "react";

type Part = { partNumber: string; displayName: string; description: string | null; idealCycleSeconds: number; isActive: boolean };
const empty = { partNumber: "", displayName: "", description: "", idealCycleSeconds: "", isActive: true };
const pageSize = 25;
const endpoint = "/api/admin/v1/parts";

export function PartMaster() {
  const [rows, setRows] = useState<Part[]>([]);
  const [offset, setOffset] = useState(0);
  const [refresh, setRefresh] = useState(0);
  const [form, setForm] = useState(empty);
  const [editing, setEditing] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError("");
    fetch(endpoint + "?offset=" + offset + "&limit=" + pageSize, { signal: controller.signal, cache: "no-store", redirect: "error" })
      .then(async response => {
        if (!response.ok) throw new Error("Part master unavailable (HTTP " + response.status + ")");
        const data: unknown = await response.json();
        if (!Array.isArray(data) || !data.every(p => p && typeof p.partNumber === "string" && typeof p.displayName === "string" && typeof p.idealCycleSeconds === "number" && typeof p.isActive === "boolean" && (p.description === null || typeof p.description === "string"))) throw new Error("Invalid part master response");
        if (!controller.signal.aborted) setRows(data);
      })
      .catch((e: unknown) => { if (!controller.signal.aborted) { setRows([]); setError(e instanceof Error ? e.message : "Part master unavailable"); } })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [offset, refresh]);
  async function save() {
    const cycle = Number(form.idealCycleSeconds);
    if (!form.partNumber.trim() || !form.displayName.trim() || !/^\d+(\.\d{1,3})?$/.test(form.idealCycleSeconds) || cycle <= 0 || cycle > 86400) {
      setError("Enter a part number, name and valid cycle time (0.001–86400 seconds)."); return;
    }
    setSaving(true); setError(""); setNotice("");
    try {
      const response = await fetch(endpoint, { method: "PUT", redirect: "error", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...form, partNumber: form.partNumber.trim(), displayName: form.displayName.trim(), description: form.description.trim() || null, idealCycleSeconds: cycle }) });
      if (!response.ok) throw new Error("Save failed (HTTP " + response.status + ")");
      setForm(empty); setEditing(false); setNotice("Part saved."); setRefresh(v => v + 1);
    } catch (e: unknown) { setError(e instanceof Error ? e.message : "Save failed"); }
    finally { setSaving(false); }
  }
  return <section className="supervisor-editor" aria-label="Part master">
    <h2>Part Master</h2>
    <p>Maintain default ideal cycle times in seconds. Existing production records are not changed.</p>
    {error && <p role="alert">{error}</p>}
    {notice && <p role="status">{notice}</p>}
    <form onSubmit={e => { e.preventDefault(); if (!saving) void save(); }}>
      <fieldset disabled={saving}>
        <legend>{editing ? "Edit part" : "Create or update part"}</legend>
        <label>Part number<input required maxLength={200} readOnly={editing} placeholder="e.g. GEAR-001" value={form.partNumber} onChange={e => setForm({ ...form, partNumber: e.target.value })} /></label>
        <label>Part name<input required maxLength={200} placeholder="e.g. Transmission gear" value={form.displayName} onChange={e => setForm({ ...form, displayName: e.target.value })} /></label>
        <label>Description<input maxLength={1000} placeholder="e.g. CNC machined helical gear" value={form.description} onChange={e => setForm({ ...form, description: e.target.value })} /></label>
        <label>Default ideal cycle (seconds)<input required type="number" min="0.001" max="86400" step="0.001" placeholder="e.g. 600" value={form.idealCycleSeconds} onChange={e => setForm({ ...form, idealCycleSeconds: e.target.value })} /></label>
        <label className="production-backdate"><input type="checkbox" checked={form.isActive} onChange={e => setForm({ ...form, isActive: e.target.checked })} />Active</label>
        <button type="submit">{saving ? "Saving…" : "Save part"}</button>
        <button type="button" onClick={() => { setForm(empty); setEditing(false); }}>Clear</button>
      </fieldset>
    </form>
    {loading ? <p role="status">Loading parts…</p> : <div style={{ overflowX: "auto" }}><table><thead><tr><th>Part number</th><th>Name</th><th>Cycle (s)</th><th>Status</th><th>Action</th></tr></thead>
      <tbody>{rows.map(p => <tr key={p.partNumber}><td>{p.partNumber}</td><td>{p.displayName}</td><td>{p.idealCycleSeconds}</td><td>{p.isActive ? "Active" : "Inactive"}</td><td><button type="button" disabled={saving} onClick={() => { setForm({ ...p, description: p.description ?? "", idealCycleSeconds: String(p.idealCycleSeconds) }); setEditing(true); }}>Edit</button></td></tr>)}</tbody></table>
      {rows.length === 0 && <p>No parts on this page.</p>}</div>}
    <button type="button" disabled={loading || offset === 0} onClick={() => setOffset(v => Math.max(0, v - pageSize))}>Previous</button>
    <span> Page {Math.floor(offset / pageSize) + 1} </span>
    <button type="button" disabled={loading || rows.length < pageSize} onClick={() => setOffset(v => v + pageSize)}>Next</button>
    <button type="button" disabled={loading} onClick={() => setRefresh(v => v + 1)}>Refresh</button>
  </section>;
}
