// Removes the previous build from the bridge's wwwroot before a new one – only what Vite writes
// (index.html, assets/), never models/ or anything else that lives there.
import { rmSync } from "node:fs";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../src/TeslaBridge/wwwroot/", import.meta.url));
for (const p of ["index.html", "assets"]) rmSync(root + p, { recursive: true, force: true });
