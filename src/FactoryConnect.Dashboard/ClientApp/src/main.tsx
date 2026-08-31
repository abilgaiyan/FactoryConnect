import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./App.tsx";
import { createDashboardApplicationRuntime } from "./application/application-runtime.ts";

const rootElement = document.getElementById("root");
if (rootElement === null) {
  throw new Error("Dashboard root element was not found.");
}

const runtime = await createDashboardApplicationRuntime(window.location.origin);

createRoot(rootElement).render(
  <StrictMode>
    <App runtime={runtime} />
  </StrictMode>,
);
