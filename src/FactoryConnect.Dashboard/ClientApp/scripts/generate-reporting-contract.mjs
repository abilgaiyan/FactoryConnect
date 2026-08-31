import { spawnSync } from "node:child_process";
import { access, mkdir, readFile, unlink, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import openapiTS, { astToString } from "openapi-typescript";

const generatedMarker =
  "// Generated from the FactoryConnect.Api OpenAPI contract.\n" +
  "// Do not edit manually.\n\n";

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

await unlink(openApiDocument).catch((error) => {
  if (error?.code !== "ENOENT") {
    throw error;
  }
});

const build = spawnSync("dotnet", ["build", apiProject, "--no-incremental"], {
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

try {
  await access(openApiDocument);
} catch {
  throw new Error(
    `FactoryConnect.Api build did not generate the expected OpenAPI document: ${openApiDocument}`,
  );
}

const ast = await openapiTS(pathToFileURL(openApiDocument));
const generatedText = generatedMarker + astToString(ast);

if (!generatedText.startsWith(generatedMarker)) {
  throw new Error("Generated reporting contract is missing the required generated-file marker.");
}

await mkdir(generatedDirectory, { recursive: true });
await writeFile(generatedContract, generatedText, "utf8");

const persistedContract = await readFile(generatedContract, "utf8");
if (!persistedContract.startsWith(generatedMarker)) {
  throw new Error("Persisted reporting contract is missing the required generated-file marker.");
}

console.log(`Generated ${generatedContract}`);
