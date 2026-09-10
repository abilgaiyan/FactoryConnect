import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

import { transformWithOxc } from "vite";

export async function load(url, context, nextLoad) {
  if (!url.endsWith(".tsx")) {
    return nextLoad(url, context);
  }

  const filename = fileURLToPath(url);
  const source = await readFile(filename, "utf8");
  const transformed = await transformWithOxc(source, filename, {
    lang: "tsx",
    jsx: {
      runtime: "automatic",
    },
  });

  return {
    format: "module",
    source: transformed.code,
    shortCircuit: true,
  };
}
