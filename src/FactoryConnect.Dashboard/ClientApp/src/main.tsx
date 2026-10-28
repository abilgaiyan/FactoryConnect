import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./App.tsx";
import { createDashboardApplicationRuntime } from "./application/application-runtime.ts";
import "./presentation/dashboard.css";
import "./presentation/daily-report.css";
import "./presentation/fleet-range.css";

const rootElement = document.getElementById("root");
if (rootElement === null) {
  throw new Error("Dashboard root element was not found.");
}

if (import.meta.env.DEV && window.location.pathname === "/supervisor-preview") {
  const { default: SupervisorDeskPreview } = await import("./presentation/SupervisorDeskPreview.tsx");
  createRoot(rootElement).render(
    <div className="dashboard-shell"><header><p>FactoryConnect</p><nav aria-label="Screen review"><a href="/">Return to dashboard</a></nav></header><main id="main-content"><SupervisorDeskPreview /></main></div>,
  );
} else {
  const runtime = await createDashboardApplicationRuntime(window.location.origin);
  createRoot(rootElement).render(<StrictMode><App runtime={runtime} /></StrictMode>);
}
