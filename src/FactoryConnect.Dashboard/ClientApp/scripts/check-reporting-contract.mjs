import { spawnSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { dirname, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const clientDirectory = resolve(scriptDirectory, "..");
const generatorScript = resolve(scriptDirectory, "generate-reporting-contract.mjs");
const generatedContract = resolve(
  clientDirectory,
  "src",
  "api",
  "generated",
  "reporting-contract.ts",
);
const typeScriptCompiler = resolve(
  clientDirectory,
  "node_modules",
  "typescript",
  "bin",
  "tsc",
);

function run(command, args, options = {}) {
  const result = spawnSync(command, args, {
    cwd: clientDirectory,
    stdio: "inherit",
    shell: false,
    ...options,
  });

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }

  return result;
}

const repositoryRootResult = spawnSync("git", ["rev-parse", "--show-toplevel"], {
  cwd: clientDirectory,
  encoding: "utf8",
  shell: false,
});

if (repositoryRootResult.error) {
  throw repositoryRootResult.error;
}

if (repositoryRootResult.status !== 0) {
  throw new Error("Unable to resolve the FactoryConnect repository root.");
}

const repositoryRoot = repositoryRootResult.stdout.trim();
const repositoryRelativeContract = relative(repositoryRoot, generatedContract)
  .split(sep)
  .join("/");
const committedContractResult = spawnSync(
  "git",
  ["show", `HEAD:${repositoryRelativeContract}`],
  {
    cwd: repositoryRoot,
    encoding: null,
    shell: false,
    maxBuffer: 10 * 1024 * 1024,
  },
);

if (committedContractResult.error) {
  throw committedContractResult.error;
}

if (committedContractResult.status !== 0) {
  throw new Error(
    `Unable to read committed reporting contract from HEAD:${repositoryRelativeContract}.`,
  );
}

run(process.execPath, [generatorScript]);
const firstGeneration = await readFile(generatedContract);

run(process.execPath, [typeScriptCompiler, "--noEmit"]);

run(process.execPath, [generatorScript]);
const secondGeneration = await readFile(generatedContract);

if (!firstGeneration.equals(secondGeneration)) {
  throw new Error(
    "Reporting contract generation is not deterministic: consecutive generations differ.",
  );
}

if (!committedContractResult.stdout.equals(secondGeneration)) {
  throw new Error(
    "Generated reporting contract differs from the committed artifact. Run npm run contracts:generate and commit the result.",
  );
}

console.log("Reporting contract is deterministic, type-safe, and synchronized with the committed artifact.");
