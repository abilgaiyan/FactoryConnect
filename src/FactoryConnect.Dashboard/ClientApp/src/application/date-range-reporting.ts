import type { DashboardRuntimeSource } from './runtime-configuration.ts';
import { isProductionDaySelection, queryAuthoritativeProductionDay } from './production-day-reporting.ts';
import type { ReportingClient, OperationalMetricPage } from '../api/reporting/index.ts';
export const rangeMetrics = ['availability', 'utilization.elr', 'performance', 'quality', 'oee'] as const;
export type RangeItem = OperationalMetricPage['items'][number];
export interface RangeRow {
    source: DashboardRuntimeSource;
    day: string;
    items: RangeItem[];
}
export function selectedDays(from: string, through: string): string[] {
    if (!isProductionDaySelection(from) || !isProductionDaySelection(through) || from > through)
        throw new Error('Select a valid inclusive date range.');
    const days: string[] = [];
    for (let day = from; day <= through;) {
        days.push(day);
        const next = new Date(`${day}T00:00:00Z`);
        next.setUTCDate(next.getUTCDate() + 1);
        day = next.toISOString().slice(0, 10);
    }
    return days;
}
export function reportingIdentity(item: RangeItem): string {
    return JSON.stringify([item.processorId, item.machineId.toLowerCase(), item.productionDay?.siteId, item.productionDay?.businessDate,
        item.context.productionOrderId, item.context.operationId, item.context.partId, item.context.operatorId, item.metricKey, item.definitionVersion]);
}
export function exactItemContent(item: RangeItem): string {
    return JSON.stringify([reportingIdentity(item), item.scope, item.shift, item.status, item.value, item.unit, item.reasonCode, item.reasonOperandName,
        item.sourceRevision.processorId, item.sourceRevision.machineId.toLowerCase(), item.sourceRevision.streamKey, item.sourceRevision.position]);
}
export async function loadRange(from: string, through: string, sources: readonly DashboardRuntimeSource[], client: ReportingClient, signal: AbortSignal): Promise<RangeRow[]> {
    const rows: RangeRow[] = [];
    const days = selectedDays(from, through);
    for (const source of sources)
        for (const day of days) {
            signal.throwIfAborted();
            const result = await queryAuthoritativeProductionDay(day, [source], client, { signal });
            const exact = new Map<string, RangeItem>();
            for (const item of result.items) {
                if (item.scope !== 'production-day' || item.machineId.toLowerCase() !== source.machineId.toLowerCase() || item.processorId !== source.processorId
                    || item.productionDay?.businessDate !== day || item.productionDay.siteId !== source.siteId
                    || item.sourceRevision.machineId.toLowerCase() !== source.machineId.toLowerCase()
                    || Object.values(item.context).some(value => value !== null))
                    throw new Error('Reporting identity does not match the selection.');
                const key = reportingIdentity(item);
                const previous = exact.get(key);
                if (previous && exactItemContent(previous) !== exactItemContent(item))
                    throw new Error('Conflicting results for the same exact reporting identity.');
                exact.set(key, item);
            }
            rows.push({ source, day, items: [...exact.values()] });
        }
    return rows;
}
export function trendSegments(values: readonly (number | null)[]): {
    x: number;
    y: number;
}[][] {
    const segments: {
        x: number;
        y: number;
    }[][] = [];
    let current: {
        x: number;
        y: number;
    }[] = [];
    values.forEach((value, x) => { if (value === null || !Number.isFinite(value)) {
        if (current.length)
            segments.push(current);
        current = [];
    }
    else
        current.push({ x, y: value }); });
    if (current.length)
        segments.push(current);
    return segments;
}
// Coordinates are approximate; table values retain the exact transport representation.
export function plottedValue(item: RangeItem | undefined): number | null {
    if (item?.status !== 'calculated' || item.value === null || item.value === undefined)
        return null;
    const value = Number(item.value);
    if (!Number.isFinite(value) || (value === 0 && !/^[-+]?0*(?:\.0*)?(?:[eE][+-]?\d+)?$/.test(String(item.value))))
        return null;
    return value;
}
