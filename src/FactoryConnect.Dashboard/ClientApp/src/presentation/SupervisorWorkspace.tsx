import { useEffect, useState } from "react";
import { createDashboardApplicationRuntime, type DashboardApplicationRuntime } from "../application/application-runtime.ts";
import { SupervisorReadOnly } from "./SupervisorReadOnly.tsx";
import "./supervisor-preview.css";

export default function SupervisorWorkspace({ runtime }: { readonly runtime?: DashboardApplicationRuntime }) {
  return <section className="supervisor-preview" aria-label="Supervisor workspace">
    <h1>Supervisor</h1>
    <RuntimeReadOnly {...(runtime ? { runtime } : {})} />
  </section>;
}
function RuntimeReadOnly({ runtime }: { readonly runtime?: DashboardApplicationRuntime }) {
  const [loaded, setLoaded] = useState<DashboardApplicationRuntime | null>(runtime ?? null);
  const [error, setError] = useState(false);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (runtime) { setLoaded(runtime); return; }
    let current = true;
    setLoaded(null); setError(false);
    void createDashboardApplicationRuntime(window.location.origin).then(result => {
      if (current) setLoaded(result);
    }, () => { if (current) setError(true); });
    return () => { current = false; };
  }, [runtime, attempt]);
  return <>
    <p role="status"><strong>Configured reporting sources.</strong> Data provenance is shown on every completed read; this mode does not certify factory acceptance.</p>
    {error ? <><p role="alert">Reporting configuration is unavailable. No factory evidence established; no sample fallback.</p><button type="button" onClick={() => setAttempt(value => value + 1)}>Retry configuration</button></>
      : loaded ? <SupervisorReadOnly runtime={loaded} /> : <p role="status">Loading reporting configuration…</p>}
  </>;
}
