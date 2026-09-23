import { resolve } from "node:path";
import { fileURLToPath, URL } from "node:url";

import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const dashboardHost = "http://localhost:5090";
const dashboardOutputDirectory = process.env.FACTORYCONNECT_DASHBOARD_OUT_DIR
  ? resolve(process.env.FACTORYCONNECT_DASHBOARD_OUT_DIR)
  : fileURLToPath(new URL("../wwwroot", import.meta.url));

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": dashboardHost,
      "/dashboard": dashboardHost,
      "/health": dashboardHost,
    },
  },
  build: {
    outDir: dashboardOutputDirectory,
    emptyOutDir: true,
  },
});
