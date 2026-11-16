import { resolve } from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// The backend URL is fixed when the extension is packaged (see scripts/package.mjs), never
// read from settings at runtime: org-wide extension settings can be changed by any user.
const backendUrl = process.env.CWC_BACKEND_URL ?? "";

export default defineConfig({
  base: "./",
  plugins: [react()],
  define: {
    __BACKEND_URL__: JSON.stringify(backendUrl),
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 3000,
    rollupOptions: {
      input: {
        hub: resolve(import.meta.dirname, "hub.html"),
        settings: resolve(import.meta.dirname, "settings.html"),
      },
    },
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
  },
});
