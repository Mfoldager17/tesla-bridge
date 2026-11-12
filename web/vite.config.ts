import { defineConfig } from "vitest/config";
import solid from "vite-plugin-solid";

// The bridge serves this build from wwwroot/next (http://<server>:8090/next/) next to the current dashboard,
// until phase 2.5 makes it the main one. `npm run dev` proxies the API to a bridge on localhost:8080.
export default defineConfig({
  plugins: [solid()],
  base: "/next/",
  build: {
    outDir: "../src/TeslaBridge/wwwroot/next",
    emptyOutDir: true,
    target: "es2022",
  },
  server: {
    port: 5173,
    proxy: { "/api": { target: process.env.BRIDGE_URL ?? "http://localhost:8080", changeOrigin: true } },
  },
  test: { environment: "node" },
});
