import {
  type FormEvent,
  type MouseEvent,
  type PropsWithChildren,
  useState,
} from "react";

import type { DashboardApplicationRuntime } from "./application/application-runtime.ts";
import { ProductionDayOverviewMatrix } from "./application/ProductionDayOverviewMatrix.ts";
import type { ProductionDayOverviewViewState } from "./application/production-day-overview-state.ts";
import { isProductionDaySelection } from "./application/production-day-reporting.ts";
import { useProductionDayOverview } from "./application/use-production-day-overview.ts";
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
          <ApplicationLink href="/" navigate={navigate} current={route.kind === "productionDayOverview"}>
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
    if (!shouldHandleApplicationNavigation(event, {
      href: anchor.href,
      target: anchor.target,
      hasDownload: anchor.hasAttribute("download"),
    }, window.location.origin)) {
      return;
    }

    event.preventDefault();
    navigate(anchor.href);
  };

  return <a href={href} aria-current={current ? "page" : undefined} onClick={handleClick}>{children}</a>;
}

interface RouteViewProps {
  readonly route: ApplicationRoute;
  readonly navigate: (href: string) => void;
  readonly runtime: DashboardApplicationRuntime;
}

function RouteView({ route, navigate, runtime }: RouteViewProps) {
  switch (route.kind) {
    case "productionDayOverview":
      return <ProductionDaySelection navigate={navigate} sourceCount={runtime.configuration.sources.length} />;
    case "productionDayDetail":
      return <ProductionDayDetail productionDay={route.productionDay} navigate={navigate} runtime={runtime} />;
    case "machineDetail":
      return <section aria-labelledby="route-title"><RouteContext current="Machine" navigate={navigate} /><h1 id="route-title">Machine</h1><p>{route.machineId}</p><p>Machine detail placeholder.</p></section>;
    case "dailyReport":
      return <section aria-labelledby="route-title"><RouteContext current="Daily report" navigate={navigate} /><h1 id="route-title">Daily report</h1><p>{route.productionDay}</p><p>Daily-report placeholder.</p></section>;
    case "notFound":
      return <section aria-labelledby="route-title"><RouteContext current="Not found" navigate={navigate} /><h1 id="route-title">Page not found</h1><p>The dashboard has no route for this path.</p><code>{route.path}</code></section>;
  }
}

interface ProductionDaySelectionProps {
  readonly navigate: (href: string) => void;
  readonly sourceCount: number;
}

function ProductionDaySelection({ navigate, sourceCount }: ProductionDaySelectionProps) {
  const [productionDay, setProductionDay] = useState("");
  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (isProductionDaySelection(productionDay)) {
      navigate(productionDayPath(productionDay));
    }
  };

  return (
    <section aria-labelledby="route-title">
      <h1 id="route-title">Production days</h1>
      <p>{sourceCount} configured reporting source{sourceCount === 1 ? "" : "s"}.</p>
      <form onSubmit={handleSubmit}>
        <label htmlFor="production-day">Production day</label>
        <input id="production-day" name="production-day" type="date" required value={productionDay} onChange={(event) => setProductionDay(event.currentTarget.value)} />
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
  const handleDayChange = (value: string) => {
    if (isProductionDaySelection(value) && value !== productionDay) {
      navigate(productionDayPath(value));
    }
  };

  return (
    <section aria-labelledby="route-title">
      <RouteContext current="Production day" navigate={navigate} />
      <h1 id="route-title">Production day</h1>
      <label htmlFor="production-day-overview-selector">Production day</label>
      <input
        id="production-day-overview-selector"
        name="production-day-overview-selector"
        type="date"
        value={productionDay}
        onChange={(event) => handleDayChange(event.currentTarget.value)}
      />
      {isProductionDaySelection(productionDay)
        ? <ProductionDayOverviewVertical key={productionDay} productionDay={productionDay} runtime={runtime} />
        : <p role="alert">The selected production day is not a valid calendar date.</p>}
    </section>
  );
}

interface ProductionDayOverviewVerticalProps {
  readonly productionDay: string;
  readonly runtime: DashboardApplicationRuntime;
}

function ProductionDayOverviewVertical({ productionDay, runtime }: ProductionDayOverviewVerticalProps) {
  const overview = useProductionDayOverview(productionDay, runtime);
  const refresh = () => {
    void overview.refresh();
  };

  return (
    <div aria-busy={overview.state.kind === "loading" ? "true" : "false"}>
      <button type="button" onClick={refresh} disabled={overview.state.kind === "loading"}>Refresh</button>
      {overview.lastSuccessfulRetrieval === null ? null : (
        <p>
          Last loaded for {overview.lastSuccessfulRetrieval.productionDay}: {overview.lastSuccessfulRetrieval.retrievedAt.toLocaleString()}
        </p>
      )}
      <ProductionDayOverviewStateView state={overview.state} />
    </div>
  );
}

function ProductionDayOverviewStateView({ state }: { readonly state: ProductionDayOverviewViewState }) {
  switch (state.kind) {
    case "idle":
    case "loading":
      return <p role="status">Loading production-day reporting…</p>;
    case "empty-factory":
      return <p>No machines are configured for this dashboard.</p>;
    case "success":
      return <ProductionDayOverviewMatrix model={state.model} />;
    case "request-invalid":
    case "reporting-failed":
    case "presentation-failed":
      return <p role="alert">{state.message}</p>;
  }
}

export function productionDayPath(productionDay: string): string {
  return `/production-days/${encodeURIComponent(productionDay)}`;
}

interface RouteContextProps {
  readonly current: string;
  readonly navigate: (href: string) => void;
}

function RouteContext({ current, navigate }: RouteContextProps) {
  return <nav aria-label="Breadcrumb"><ApplicationLink href="/" navigate={navigate}>Production days</ApplicationLink><span aria-current="page">{current}</span></nav>;
}
