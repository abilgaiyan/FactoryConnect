import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

test("Daily Report modules use only the exact approved dependency set", () => {
  const modules = [
    "src/application/daily-report-lifecycle.ts",
    "src/application/use-daily-report.ts",
    "src/presentation/daily-report-composer.ts",
    "src/presentation/daily-report-page-policy.ts",
    "src/presentation/DailyReportPage.tsx",
    "src/application/daily-report-navigation.ts",
  ];

  const allowedDependencies = new Set([
    "react",

    "../api/reporting/index.ts",
    "../routing/application-route.ts",

    "./application-runtime.ts",
    "./daily-report-lifecycle.ts",
    "./production-day-presentation.ts",
    "./production-day-reporting.ts",
    "./production-day-shift-reporting.ts",
    "./runtime-configuration.ts",

    "../application/daily-report-lifecycle.ts",
    "../application/production-day-presentation.ts",

    "../presentation/daily-report-composer.ts",
    "../presentation/daily-report-model.ts",
    "../presentation/shift-performance-model.ts",

    "./daily-report-model.ts",
    "./daily-report-page-policy.ts",
    "./shift-performance-model.ts",
    "./shift-performance-projector.ts",
  ]);

  for (const modulePath of modules) {
    const source = fs.readFileSync(new URL(`../${modulePath}`, import.meta.url), "utf8");
    const imports = [...source.matchAll(/from\s+["']([^"']+)["']/g)].map((match) => match[1]);

    for (const dependency of imports) {
      assert.ok(
        allowedDependencies.has(dependency),
        `${modulePath} imports disallowed Daily Report dependency ${dependency}`,
      );
    }
  }
});
