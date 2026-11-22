import assert from "node:assert/strict";
import { register } from "node:module";
import test from "node:test";

register("./tsx-test-loader.mjs", import.meta.url);
const { sourceIntegrityGaps } = await import("../src/presentation/supervisor-source-readiness.ts");
const start = "2026-11-22T07:30:00Z";
const end = "2026-11-22T16:00:00Z";
const source = (overrides = {}) => ({
  evidenceId: "e-1", occurredAtUtc: "2026-11-22T08:00:00Z",
  partCountIncrement: 2, goodQuantity: 2, rejectedQuantity: 0,
  productionOrderId: "order", partId: "part", operationId: "op",
  contributingPositions: ["10"], ...overrides
});

test("distinct sources inside the shift have no detected integrity gaps but are not qualified", () => {
  assert.deepEqual(sourceIntegrityGaps([source(), source({ evidenceId: "e-2", contributingPositions: ["11"] })], start, end), []);
});
test("detects duplicate IDs and CNC positions, including repetition within one source", () => {
  const gaps = sourceIntegrityGaps([source({ contributingPositions: ["10", "10"] }),
    source({ contributingPositions: ["10"] })], start, end);
  assert.ok(gaps.some(g => g.includes("Duplicate evidence ID")));
  assert.equal(gaps.filter(g => g.includes("Repeated CNC position")).length, 2);
});
test("detects absent CNC positions, outside-shift timestamps and invalid windows", () => {
  const gaps = sourceIntegrityGaps([source({ contributingPositions: [], occurredAtUtc: end })], start, end);
  assert.ok(gaps.some(g => g.includes("No contributing CNC positions")));
  assert.ok(gaps.some(g => g.includes("outside selected shift")));
  assert.ok(sourceIntegrityGaps([source()], end, start).some(g => g.includes("invalid shift window")));
});
test("rejects unsafe quantity decomposition without rounding or publishing", () => {
  const gaps = sourceIntegrityGaps([source({ goodQuantity: Number.MAX_SAFE_INTEGER, rejectedQuantity: 1 })], start, end);
  assert.ok(gaps.some(g => g.includes("safe integer range")));
});
