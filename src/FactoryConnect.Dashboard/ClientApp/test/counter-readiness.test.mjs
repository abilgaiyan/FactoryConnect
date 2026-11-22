import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import { createServer } from "vite";

const vite = await createServer({
  root: fileURLToPath(new URL("../", import.meta.url)),
  server: { middlewareMode: true }, appType: "custom", logLevel: "silent"
});
const { counterReadiness } = await vite.ssrLoadModule("/src/application/counter-readiness.ts");
after(() => vite.close());

test("empty counter history is not treated as zero produced quantity", () => {
  const result = counterReadiness({ samples: [] });
  assert.equal(result.status, "NoSamples");
  assert.equal(result.samples, 0);
  assert.match(result.reasons.join(" "), /No selected counter observations/);
  assert.match(result.reasons.join(" "), /not proven equivalent/);
});

test("counter anomalies are counted but never admitted as production", () => {
  const result = counterReadiness({ samples: [
    { quality: "Good", flags: ["CounterDecreased", "InstanceChanged"] },
    { quality: "Bad", flags: ["NonIncreasingTimestamp"] }
  ] });
  assert.equal(result.status, "EvidenceNeedsReview");
  assert.equal(result.decreases, 1);
  assert.equal(result.instanceChanges, 1);
  assert.equal(result.nonGood, 1);
  assert.equal(result.nonIncreasingTimestamps, 1);
  assert.equal("acceptedQuantity" in result, false);
});
