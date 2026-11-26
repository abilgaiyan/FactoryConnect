import { ScheduleMaster } from "./ScheduleMaster.tsx";
import { DowntimeReasonAdministration } from "./DowntimeReasonAdministration.tsx";
import "./supervisor-preview.css";

export default function MastersPage() {
  return <section className="supervisor-preview" aria-label="Masters workspace">
    <h1>Masters</h1>
    <section aria-label="General configuration">
      <h2>General configuration</h2>
      <dl><dt>Current dashboard display time zone</dt><dd>Indian Standard Time (IST) — Asia/Kolkata (UTC+05:30)</dd>
      <dt>Storage and audit time</dt><dd>UTC</dd></dl>
      <p>Display setting only. Site schedule authority and historical shift boundaries are unchanged. Editable site configuration is not yet available.</p>
    </section>
    <ScheduleMaster />
    <DowntimeReasonAdministration />
  </section>;
}
