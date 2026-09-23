// Build oracle for the audio worklets: Vite must emit them as separate JavaScript chunks. A bare
// `new URL("./x.ts", import.meta.url)` makes Vite inline the raw .ts as `data:video/mp2t`, which
// audioWorklet.addModule rejects — the dev server hides this, only the production build shows it.
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

const assets = join(import.meta.dir, "..", "dist", "assets");
const files = readdirSync(assets);
const failures: string[] = [];

for (const worklet of ["capture-processor", "playback-processor"]) {
  if (!files.some((file) => file.startsWith(`${worklet}-`) && file.endsWith(".js"))) {
    failures.push(`missing worklet chunk dist/assets/${worklet}-*.js`);
  }
}
const captureChunk = files.find(
  (file) => file.startsWith("capture-processor-") && file.endsWith(".js"),
);
if (
  !captureChunk ||
  !readFileSync(join(assets, captureChunk), "utf8").includes("presenter-echo-gate-v1")
)
  failures.push("capture worklet chunk does not bundle the echo-gate sentinel");
for (const file of files.filter((name) => name.endsWith(".js"))) {
  const source = readFileSync(join(assets, file), "utf8");
  if (source.includes("data:video/mp2t")) {
    failures.push(`${file} inlines a .ts asset as data:video/mp2t`);
  }
  if (source.includes("react-dom.development.js") || source.includes("react-dom-client.development.js")) {
    failures.push(`${file} contains a development React bundle`);
  }
}

if (failures.length > 0) {
  console.error(`check-dist: ${failures.join("; ")}`);
  process.exit(1);
}
console.log(`check-dist: ok (${files.length} assets, both worklet chunks present)`);
