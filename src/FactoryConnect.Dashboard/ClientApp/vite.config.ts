import { fileURLToPath, URL } from "node:url";

import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const dashboardHost = "http://localhost:5090";

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
    outDir: fileURLToPath(new URL("../wwwroot", import.meta.url)),
    emptyOutDir: true,
  },
});
