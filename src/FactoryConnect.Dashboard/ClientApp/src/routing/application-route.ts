export type ApplicationRoute =
  | { kind: "productionDayOverview" }
  | { kind: "productionDayDetail"; productionDay: string }
  | { kind: "shiftPerformance"; productionDay: string }
  | { kind: "machineDetail"; machineId: string }
  | { kind: "dailyReport"; productionDay: string }
  | { kind: "notFound"; path: string };

export const applicationBasePath = "/";

export function parseApplicationRoute(pathname: string): ApplicationRoute {
  if (pathname === "/") {
    return { kind: "productionDayOverview" };
  }

  if (!pathname.startsWith("/")) {
    return { kind: "notFound", path: pathname };
  }

  const rawSegments = pathname.slice(1).split("/");
  const segments = decodeSegments(rawSegments);
  if (segments === null) {
    return { kind: "notFound", path: pathname };
  }

  const first = segments[0];
  const second = segments[1];
  const third = segments[2];

  if (
    segments.length === 2 &&
    first === "production-days" &&
    second !== undefined &&
    second !== ""
  ) {
    return { kind: "productionDayDetail", productionDay: second };
  }

  if (
    segments.length === 3 &&
    first === "production-days" &&
    second !== undefined &&
    second !== "" &&
    third === "shifts"
  ) {
    return { kind: "shiftPerformance", productionDay: second };
  }

  if (
    segments.length === 2 &&
    first === "machines" &&
    second !== undefined &&
    second !== ""
  ) {
    return { kind: "machineDetail", machineId: second };
  }

  if (
    segments.length === 3 &&
    first === "production-days" &&
    second !== undefined &&
    second !== "" &&
    third === "report"
  ) {
    return { kind: "dailyReport", productionDay: second };
  }

  return { kind: "notFound", path: pathname };
}

export function routePath(route: Exclude<ApplicationRoute, { kind: "notFound" }>): string {
  switch (route.kind) {
    case "productionDayOverview":
      return "/";
    case "productionDayDetail":
      return `/production-days/${encodeURIComponent(route.productionDay)}`;
    case "shiftPerformance":
      return `/production-days/${encodeURIComponent(route.productionDay)}/shifts`;
    case "machineDetail":
      return `/machines/${encodeURIComponent(route.machineId)}`;
    case "dailyReport":
      return `/production-days/${encodeURIComponent(route.productionDay)}/report`;
  }
}

function decodeSegments(rawSegments: readonly string[]): string[] | null {
  const decoded: string[] = [];
  try {
    for (const segment of rawSegments) {
      decoded.push(decodeURIComponent(segment));
    }
  } catch (error) {
    if (error instanceof URIError) {
      return null;
    }

    throw error;
  }

  return decoded;
}
