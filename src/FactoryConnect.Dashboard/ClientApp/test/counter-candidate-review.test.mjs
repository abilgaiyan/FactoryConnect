import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createServer } from "vite";

const vite = await createServer({
  root: fileURLToPath(new URL("../", import.meta.url)),
  server: { middlewareMode: true }, appType: "custom", logLevel: "silent"
});
const { reviewCounterCandidates } = await vite.ssrLoadModule("/src/application/counter-candidate-review.ts");
after(() => vite.close());

test("no configured counter and no raw observations stay unresolved", () => {
  assert.equal(reviewCounterCandidates(null, []).status, "NoConfiguredCounter");
  assert.equal(reviewCounterCandidates("count", []).status, "NoSignalsDiscovered");
});
test("raw address matching is literal and never verified", () => {
  assert.equal(reviewCounterCandidates("count", [{ source: "mtconnect", address: "COUNT" }]).status, "NoExactAddressMatch");
  const single = reviewCounterCandidates("count", [{ source: "mtconnect", address: "count" }]);
  assert.equal(single.status, "SingleUnverifiedCandidate");
  assert.equal("verified" in single, false);
});
test("identical raw addresses from different sources are ambiguous", () => {
  const result = reviewCounterCandidates("count", [
    { source: "adapter-a", address: "count" },
    { source: "adapter-b", address: "count" }
  ]);
  assert.equal(result.status, "AmbiguousCandidates");
  assert.equal(result.candidates.length, 2);
});
