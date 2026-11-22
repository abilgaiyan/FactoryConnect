/** Read-only source-integrity diagnostics. None of these checks establishes coverage, authority or accepted production. */
export type QuantitySource = {
  evidenceId: string;
  occurredAtUtc: string;
  partCountIncrement: number | null;
  goodQuantity: number | null;
  rejectedQuantity: number | null;
  productionOrderId: string | null;
  partId: string | null;
  operationId: string | null;
  contributingPositions: string[];
};

export function sourceIntegrityGaps(sources: readonly QuantitySource[], shiftStartUtc: string, shiftEndUtc: string): string[] {
  const gaps: string[] = [];
  const ids = new Set<string>();
  const positions = new Map<string, string>();
  const start = Date.parse(shiftStartUtc);
  const end = Date.parse(shiftEndUtc);
  for (const source of sources) {
    if (ids.has(source.evidenceId)) gaps.push(`Duplicate evidence ID: ${source.evidenceId}`);
    ids.add(source.evidenceId);
    const occurred = Date.parse(source.occurredAtUtc);
    if (!Number.isFinite(start) || !Number.isFinite(end) || start >= end ||
        !Number.isFinite(occurred) || occurred < start || occurred >= end)
      gaps.push(`Evidence outside selected shift or invalid shift window: ${source.evidenceId}`);
    if (!source.contributingPositions.length)
      gaps.push(`No contributing CNC positions: ${source.evidenceId}`);
    for (const position of source.contributingPositions) {
      const previous = positions.get(position);
      if (previous !== undefined)
        gaps.push(`Repeated CNC position ${position} in ${previous} and ${source.evidenceId}`);
      else positions.set(position, source.evidenceId);
    }
    if (source.goodQuantity !== null && source.rejectedQuantity !== null &&
        !Number.isSafeInteger(source.goodQuantity + source.rejectedQuantity))
      gaps.push(`Quantity decomposition exceeds safe integer range: ${source.evidenceId}`);
  }
  return gaps;
}
