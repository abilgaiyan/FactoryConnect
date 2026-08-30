import { spawnSync } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import openapiTS, { astToString } from "openapi-typescript";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const clientDirectory = resolve(scriptDirectory, "..");
const apiProject = resolve(
  clientDirectory,
  "..",
  "..",
  "FactoryConnect.Api",
  "FactoryConnect.Api.csproj",
);
const openApiDocument = resolve(
  clientDirectory,
  "..",
  "..",
  "FactoryConnect.Api",
  "obj",
  "openapi",
  "factoryconnect-api-v1.json",
);
const generatedDirectory = resolve(clientDirectory, "src", "api", "generated");
const generatedContract = resolve(generatedDirectory, "reporting-contract.ts");

const build = spawnSync("dotnet", ["build", apiProject], {
  cwd: clientDirectory,
  stdio: "inherit",
  shell: false,
});

if (build.error) {
  throw build.error;
}

if (build.status !== 0) {
  process.exit(build.status ?? 1);
}

const ast = await openapiTS(new URL(`file://${openApiDocument.replaceAll("\\", "/")}`), {
  inject:
    "// Generated from the FactoryConnect.Api OpenAPI contract.\n// Do not edit manually.\n",
});

await mkdir(generatedDirectory, { recursive: true });
await writeFile(generatedContract, astToString(ast), "utf8");

console.log(`Generated ${generatedContract}`);
