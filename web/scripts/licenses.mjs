// Writes the licences of the libraries bundled into the dashboard next to it (MIT asks for the notice
// to travel with the code): wwwroot/assets/THIRD-PARTY-LICENSES.txt
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";

const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const pkg = JSON.parse(readFileSync(here("../package.json"), "utf8"));
let out = "Third-party software bundled in the Tesla Bridge dashboard\n";
for (const name of Object.keys(pkg.dependencies ?? {}).sort()) {
  const dir = here(`../node_modules/${name}/`);
  const meta = JSON.parse(readFileSync(dir + "package.json", "utf8"));
  const file = ["LICENSE", "LICENSE.md", "LICENSE.txt", "license"].map((f) => dir + f).find(existsSync);
  out += `\n${"=".repeat(72)}\n${name} ${meta.version} – ${meta.license}\n${meta.homepage ?? ""}\n\n${file ? readFileSync(file, "utf8").trim() : `License: ${meta.license}`}\n`;
}
writeFileSync(here("../../src/TeslaBridge/wwwroot/assets/THIRD-PARTY-LICENSES.txt"), out);
