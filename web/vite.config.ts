import { defineConfig } from "vitest/config";
import solid from "vite-plugin-solid";

// Built into the bridge's wwwroot (served on http://<server>:8090/). wwwroot also holds files that aren't
// built (models/ with the bundled Model Y), so the build only replaces index.html and assets/ – see
// scripts/clean.mjs. `npm run dev` proxies the API to a bridge on localhost:8080.
export default defineConfig({
  plugins: [solid()],
  base: "/",
  build: {
    outDir: "../src/TeslaBridge/wwwroot",
    emptyOutDir: false,
    target: "es2022",
    // three.js is its own chunk, only loaded when the 3D car is shown
    chunkSizeWarningLimit: 700,
  },
  server: {
    port: 5173,
    proxy: { "/api": { target: process.env.BRIDGE_URL ?? "http://localhost:8080", changeOrigin: true } },
  },
  test: { environment: "node" },
});
