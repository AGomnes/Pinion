# Proof — Pinion on a real e-commerce codebase

A full end-to-end run on **[nopCommerce](https://github.com/nopSolutions/nopCommerce)** — a large, real,
untested .NET e-commerce platform (tax, pricing, orders, discounts). Nothing here is staged: the numbers
are from `Nop.Services` exactly as it ships. Everything below ran **locally, offline, with no API key,
for $0** (the deterministic tier).

Reproduce: `pinion analyze <Nop.Services.csproj>` → `pinion generate … -p <host.csproj>` → `pinion verify <host.csproj>`.

---

## 1. `analyze` — the Migration Readiness Audit (free)

```
MIGRATION READINESS REPORT — Nop.Services.csproj
Scanned: 4,072 methods across 459 files

High-risk & UNPROTECTED:  2,189 methods
Migration landmines:      2 WCF
Seams to introduce:       6

TOP RISK HOTSPOTS
 1. ShoppingCartService.GetStandardWarningsAsync()  risk 7.1  ← 0 tests, complexity 59, money+auth+date, 211 lines
 2. ImportManager.PrepareImportProductDataAsync()   risk 7.1  ← 0 tests, complexity 51, money+auth+date, 319 lines
 3. ImportManager.ImportProductsFromXlsxAsync()     risk 7.0  ← 0 tests, complexity 174, money+io, 697 lines
 4. ProductService.SearchProductsAsync()            risk 7.0  ← 0 tests, complexity 92,  money+io, 330 lines
 5. ImportManager.ImportOrdersFromXlsxAsync()       risk 7.0  ← 0 tests, complexity 86,  date+io,  319 lines
```

In ~30 seconds, across 4,072 methods, Pinion finds the exact spots a migration is most likely to break:
**2,189 high-risk methods with zero tests** — money/auth/date logic, a 697-line method at cyclomatic
complexity 174 — plus **2 WCF contracts** (a real .NET Framework→Core landmine) and **6 places that
hard-wire dependencies and need a seam introduced first**. The full ranking is an offline, self-contained
HTML dashboard (`analyze --format html`).

## 2. `generate` — lock the current behavior (deterministic, $0)

Pointed at the top high-risk methods, Pinion synthesized xUnit characterization tests, compiled and ran
them against the real code, and captured the actual output as golden masters:

```
Done: 17/25 characterized (deterministic, offline, $0).
```

**17 of 25** real, heavily dependency-injected service methods were locked with **no AI and no cost** —
including monsters like `ImportProductsFromXlsxAsync` (complexity 174). Where a service injects collaborators
it can't construct, Pinion generates stub implementations so the method runs with real (no-op) collaborators
instead of failing on a null. The methods it can't characterize offline are reported, never silently dropped.

## 3. `verify` — prove behavior didn't change (the gate)

Re-running the locked suite against the current code:

```
BEHAVIOR VERIFICATION — nopScratch.csproj
Re-ran 17 locked method(s) against the current code.
✓ Behavior preserved: all 17 method(s) behave identically — safe to ship.   (exit 0)
```

That is the safety net in action. **Now migrate** — .NET Framework→Core, a refactor, an AI rewrite — and
re-run `verify`: any method whose behavior changed fails the build with a precise old-vs-new diff (console,
Markdown, or a self-contained HTML page). It's proof, not a prediction. Exit code is non-zero on any change,
so it drops straight into CI.

---

## What this demonstrates

- The **readiness audit** works at scale on real, gnarly business code and surfaces genuinely scary,
  untested, money-touching methods — the report a CTO signs off on.
- The **deterministic generator** locks real behavior on a heavily DI'd service layer, offline and free —
  no code leaves the machine, no API key, nothing to expense.
- The **verify gate** turns "we have tests" into "we can prove this migration changed nothing," with a
  CI-ready exit code and a shareable diff.

> Change everything, break nothing, prove it.

_(The full HTML dashboard and verification page are generated locally by the commands above; they're not
committed here because they're large and derived from a third-party codebase.)_
