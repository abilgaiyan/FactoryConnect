import {
  type FormEvent,
  type MouseEvent,
  type PropsWithChildren,
  useState,
} from "react";

import type { DashboardApplicationRuntime } from "./application/application-runtime.ts";
import { ProductionDayMetricResults } from "./application/ProductionDayMetricResults.tsx";
import { isProductionDaySelection } from "./application/production-day-reporting.ts";
import { useProductionDayReporting } from "./application/use-production-day-reporting.ts";
import { QueryStateView } from "./query/QueryStateView.tsx";
import type { ApplicationRoute } from "./routing/application-route.ts";
import { shouldHandleApplicationNavigation } from "./routing/navigation-policy.ts";
import { useApplicationRouter } from "./routing/use-application-router.ts";

export interface AppProps {
  readonly runtime: DashboardApplicationRuntime;
}

export function App({ runtime }: AppProps) {
  const { route, navigate } = useApplicationRouter();

  return (
    <div>
      <header>
        <a href="#main-content">Skip to content</a>
        <p>FactoryConnect</p>
        <nav aria-label="Dashboard">
          <ApplicationLink
            href="/"
            navigate={navigate}
            current={route.kind === "productionDayOverview"}
          >
            Production days
          </ApplicationLink>
        </nav>
      </header>

      <main id="main-content">
        <RouteView route={route} navigate={navigate} runtime={runtime} />
      </main>
    </div>
  );
}

interface ApplicationLinkProps extends PropsWithChildren {
  readonly href: string;
  readonly current?: boolean;
  readonly navigate: (href: string) => void;
}

function ApplicationLink({ href, current = false, navigate, children }: ApplicationLinkProps) {
  const handleClick = (event: MouseEvent<HTMLAnchorElement>) => {
    const anchor = event.currentTarget;
    if (
      !shouldHandleApplicationNavigation(
        event,
        {
          href: anchor.href,
          target: anchor.target,
          hasDownload: anchor.hasAttribute("download"),
        },
        window.location.origin,
      )
    ) {
      return;
    }

    event.preventDefault();
    navigate(anchor.href);
  };

  return (
    <a href={href} aria-current={current ? "page" : undefined} onClick={handleClick}>
      {children}
    </a>
  );
}

interface RouteViewProps {
  readonly route: ApplicationRoute;
  readonly navigate: (href: string) => void;
  readonly runtime: DashboardApplicationRuntime;
}

function RouteView({ route, navigate, runtime }: RouteViewProps) {
  switch (route.kind) {
    case "productionDayOverview":
      return <ProductionDayOverview navigate={navigate} sourceCount={runtime.configuration.sources.length} />;
    case "productionDayDetail":
      return (
        <ProductionDayDetail
          productionDay={route.productionDay}
          navigate={navigate}
          runtime={runtime}
        />
      );
    case "machineDetail":
      return (
        <section aria-labelledby="route-title">
          <RouteContext current="Machine" navigate={navigate} />
          <h1 id="route-title">Machine</h1>
          <p>{route.machineId}</p>
          <p>Machine detail placeholder.</p>
        </section>
      );
    case "dailyReport":
      return (
        <section aria-labelledby="route-title">
          <RouteContext current="Daily report" navigate={navigate} />
          <h1 id="route-title">Daily report</h1>
          <p>{route.productionDay}</p>
          <p>Daily-report placeholder.</p>
        </section>
      );
    case "notFound":
      return (
        <section aria-labelledby="route-title">
          <RouteContext current="Not found" navigate={navigate} />
          <h1 id="route-title">Page not found</h1>
          <p>The dashboard has no route for this path.</p>
          <code>{route.path}</code>
        </section>
      );
  }
}

interface ProductionDayOverviewProps {
  readonly navigate: (href: string) => void;
  readonly sourceCount: number;
}

function ProductionDayOverview({ navigate, sourceCount }: ProductionDayOverviewProps) {
  const [productionDay, setProductionDay] = useState("");

  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!isProductionDaySelection(productionDay)) {
      return;
    }

    navigate(`/production-days/${encodeURIComponent(productionDay)}`);
  };

  return (
    <section aria-labelledby="route-title">
      <h1 id="route-title">Production days</h1>
      <p>{sourceCount} configured reporting source{sourceCount === 1 ? "" : "s"}.</p>
      <form onSubmit={handleSubmit}>
        <label htmlFor="production-day">Production day</label>
        <input
          id="production-day"
          name="production-day"
          type="date"
          required
          value={productionDay}
          onChange={(event) => setProductionDay(event.currentTarget.value)}
        />
        <button type="submit">Load reporting data</button>
      </form>
    </section>
  );
}

interface ProductionDayDetailProps {
  readonly productionDay: string;
  readonly navigate: (href: string) => void;
  readonly runtime: DashboardApplicationRuntime;
}

function ProductionDayDetail({ productionDay, navigate, runtime }: ProductionDayDetailProps) {
  return (
    <section aria-labelledby="route-title">
      <RouteContext current="Production day" navigate={navigate} />
      <h1 id="route-title">Production day</h1>
      <p>{productionDay}</p>
      {isProductionDaySelection(productionDay) ? (
        <ProductionDayReportingProof productionDay={productionDay} runtime={runtime} />
      ) : (
        <p role="alert">The selected production day is not a valid calendar date.</p>
      )}
    </section>
  );
}

interface ProductionDayReportingProofProps {
  readonly productionDay: string;
  readonly runtime: DashboardApplicationRuntime;
}

function ProductionDayReportingProof({ productionDay, runtime }: ProductionDayReportingProofProps) {
  const query = useProductionDayReporting(productionDay, runtime);

  return (
    <QueryStateView state={query.state}>
      {(page) => (
        <ProductionDayMetricResults
          page={page}
          sources={runtime.configuration.sources}
        />
      )}
    </QueryStateView>
  );
}

interface RouteContextProps {
  readonly current: string;
  readonly navigate: (href: string) => void;
}

function RouteContext({ current, navigate }: RouteContextProps) {
  return (
    <nav aria-label="Breadcrumb">
      <ApplicationLink href="/" navigate={navigate}>
        Production days
      </ApplicationLink>
      <span aria-current="page">{current}</span>
    </nav>
  );
}
