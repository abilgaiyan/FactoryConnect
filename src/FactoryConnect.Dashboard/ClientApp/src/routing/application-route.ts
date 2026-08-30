export type ApplicationRoute =
  | { kind: "productionDayOverview" }
  | { kind: "productionDayDetail"; productionDay: string }
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

  if (
    segments.length === 2 &&
    segments[0] === "production-days" &&
    segments[1] !== ""
  ) {
    return { kind: "productionDayDetail", productionDay: segments[1] };
  }

  if (
    segments.length === 2 &&
    segments[0] === "machines" &&
    segments[1] !== ""
  ) {
    return { kind: "machineDetail", machineId: segments[1] };
  }

  if (
    segments.length === 3 &&
    segments[0] === "production-days" &&
    segments[1] !== "" &&
    segments[2] === "report"
  ) {
    return { kind: "dailyReport", productionDay: segments[1] };
  }

  return { kind: "notFound", path: pathname };
}

export function routePath(route: Exclude<ApplicationRoute, { kind: "notFound" }>): string {
  switch (route.kind) {
    case "productionDayOverview":
      return "/";
    case "productionDayDetail":
      return `/production-days/${encodeURIComponent(route.productionDay)}`;
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
