// Runs node's test runner on this folder. Ignores extra arguments: the queue runner passes vitest flags such as
// --reporter=verbose, which node --test rejects.
import { spawnSync } from "node:child_process";
import { readdirSync } from "node:fs";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const files = readdirSync(here).filter((f) => f.endsWith(".test.mjs"));
const run = spawnSync(process.execPath, ["--test", "--test-reporter=spec", ...files], { cwd: here, stdio: "inherit" });
process.exit(run.status ?? 1);
