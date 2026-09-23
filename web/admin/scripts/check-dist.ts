import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

const assets = join(import.meta.dir, "..", "dist", "assets");
const failures = readdirSync(assets)
  .filter((file) => file.endsWith(".js"))
  .filter((file) => {
    const source = readFileSync(join(assets, file), "utf8");
    return source.includes("react-dom.development.js") || source.includes("react-dom-client.development.js");
  })
  .map((file) => `${file} contains a development React bundle`);

if (failures.length > 0) {
  console.error(`check-dist: ${failures.join("; ")}`);
  process.exit(1);
}
console.log("check-dist: ok (production React bundle)");
