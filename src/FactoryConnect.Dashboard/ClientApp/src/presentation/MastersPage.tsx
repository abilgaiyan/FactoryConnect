import { DowntimeReasonAdministration } from "./DowntimeReasonAdministration.tsx";
import "./supervisor-preview.css";

export default function MastersPage() {
  return <section className="supervisor-preview" aria-label="Masters workspace">
    <h1>Masters</h1>
    <DowntimeReasonAdministration />
  </section>;
}
