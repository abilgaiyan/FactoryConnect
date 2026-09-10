import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

import { transformWithEsbuild } from "vite";

export async function load(url, context, nextLoad) {
  if (!url.endsWith(".tsx")) {
    return nextLoad(url, context);
  }

  const filename = fileURLToPath(url);
  const source = await readFile(filename, "utf8");
  const transformed = await transformWithEsbuild(source, filename, {
    loader: "tsx",
    jsx: "automatic",
    target: "es2022",
    format: "esm",
  });

  return {
    format: "module",
    source: transformed.code,
    shortCircuit: true,
  };
}
