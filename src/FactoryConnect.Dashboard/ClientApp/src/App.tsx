import type { MouseEvent, PropsWithChildren } from "react";

import type { ApplicationRoute } from "./routing/application-route.ts";
import { shouldHandleApplicationNavigation } from "./routing/navigation-policy.ts";
import { useApplicationRouter } from "./routing/use-application-router.ts";

export function App() {
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
        <RouteView route={route} navigate={navigate} />
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
    if (!shouldHandleApplicationNavigation(event, event.currentTarget, window.location.origin)) {
      return;
    }

    event.preventDefault();
    navigate(event.currentTarget.href);
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
}

function RouteView({ route, navigate }: RouteViewProps) {
  switch (route.kind) {
    case "productionDayOverview":
      return (
        <section aria-labelledby="route-title">
          <h1 id="route-title">Production days</h1>
          <p>Production-day overview placeholder.</p>
        </section>
      );
    case "productionDayDetail":
      return (
        <section aria-labelledby="route-title">
          <RouteContext current="Production day" navigate={navigate} />
          <h1 id="route-title">Production day</h1>
          <p>{route.productionDay}</p>
          <p>Production-day detail placeholder.</p>
        </section>
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
