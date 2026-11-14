import { useEffect, useState } from 'react';

type Reason = { reasonCode: string; displayName: string; description: string | null; isActive: boolean };

export function DowntimeReasonAdministration() {
  const [reasons, setReasons] = useState<Reason[]>([]);
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [active, setActive] = useState(true);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState('');
  const [loaded, setLoaded] = useState(false);

  async function load(signal?: AbortSignal) {
    const response = await fetch('/api/supervisor/v1/downtime-reasons/all', signal ? { signal } : {});
    if (!response.ok) throw new Error(`Reason master unavailable (HTTP ${response.status}).`);
    const data: unknown = await response.json();
    if (!Array.isArray(data)) throw new Error('Invalid reason master response.');
    setReasons(data as Reason[]);
    setLoaded(true);
  }
  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal).catch(error => { if (!controller.signal.aborted) setStatus(String(error)); });
    return () => controller.abort();
  }, []);

  function edit(reason: Reason) {
    setCode(reason.reasonCode); setName(reason.displayName);
    setDescription(reason.description ?? ''); setActive(reason.isActive); setStatus('');
  }
  function clear() {
    setCode(''); setName(''); setDescription(''); setActive(true);
  }
  async function save() {
    if (busy || !code.trim() || !name.trim()) return;
    setBusy(true); setStatus('');
    try {
      const response = await fetch(`/api/supervisor/v1/downtime-reasons/${encodeURIComponent(code.trim())}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ displayName: name.trim(), description: description.trim() || null, isActive: active })
      });
      if (!response.ok) throw new Error(`Reason was not confirmed saved (HTTP ${response.status}). Refresh before retrying.`);
      await load();
      clear();
      setStatus('Reason saved. Supervisor dropdown will reflect active reasons after refresh.');
    } catch (error) { setStatus(String(error)); }
    finally { setBusy(false); }
  }
  return <section aria-label="Downtime reason administration">
    <h2>Downtime reason master</h2>
    <p>Local commissioning administration. Inactive reasons remain in historical downtime explanations.</p>
    <button type="button" disabled={busy} onClick={() => void load().then(() => setStatus('Reason list refreshed.'), error => setStatus(String(error)))}>Refresh reasons</button>
    {status && <p role="status">{status}</p>}
    {loaded && <table><thead><tr><th>Code</th><th>Reason</th><th>Status</th><th>Action</th></tr></thead>
      <tbody>{reasons.map(reason => <tr key={reason.reasonCode}>
        <td>{reason.reasonCode}</td><td>{reason.displayName}</td>
        <td>{reason.isActive ? 'Active' : 'Inactive'}</td>
        <td><button type="button" disabled={busy} onClick={() => edit(reason)}>Edit</button></td>
      </tr>)}</tbody></table>}
    <fieldset disabled={busy}>
      <legend>{reasons.some(reason => reason.reasonCode === code) ? 'Edit reason' : 'New reason'}</legend>
      <label>Reason code <input value={code} maxLength={100} disabled={reasons.some(reason => reason.reasonCode === code)} onChange={event => setCode(event.target.value)} /></label>
      <label>Display name <input value={name} maxLength={200} onChange={event => setName(event.target.value)} /></label>
      <label>Description <textarea value={description} maxLength={1000} onChange={event => setDescription(event.target.value)} /></label>
      <label><input type="checkbox" checked={active} onChange={event => setActive(event.target.checked)} /> Active</label>
      <button type="button" disabled={!code.trim() || !name.trim()} onClick={() => void save()}>Save reason</button>
      <button type="button" onClick={clear}>Clear</button>
    </fieldset>
  </section>;
}
