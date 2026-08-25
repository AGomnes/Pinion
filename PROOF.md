# Proof — Pinion on a real e-commerce codebase

A full end-to-end run on **[nopCommerce](https://github.com/nopSolutions/nopCommerce)** — a large, real,
untested .NET e-commerce platform (tax, pricing, orders, discounts). Nothing here is staged: the numbers
are from `Nop.Services` exactly as it ships. Everything below ran **locally, offline, with no API key,
for $0** using the deterministic generator.

Measured on **Pinion 1.2.0**. Reproduce with:
`pinion analyze <Nop.Services.csproj>` → `pinion generate … -p <host.csproj>` → `pinion verify <host.csproj>`.

---

## 1. `analyze` — the Migration Readiness Audit

```
MIGRATION READINESS REPORT — Nop.Services.csproj
Scanned: 4,061 methods across 458 files

Behavior coverage:        0% (0 of 4,061 methods tested)
High-risk & UNPROTECTED:  2,185 methods
Seams to introduce:       6 (high-risk methods that hard-wire deps — no test seam)
Migration landmines:      2 WCF

TOP RISK HOTSPOTS
 1. ShoppingCartService.GetStandardWarningsAsync()  risk 7.1  ← 0 tests, complexity 59, money+auth+date, 211 lines
 2. ImportManager.PrepareImportProductDataAsync()   risk 7.1  ← 0 tests, complexity 51, money+auth+date, 319 lines
 3. ImportManager.ImportProductsFromXlsxAsync()     risk 7.0  ← 0 tests, complexity 174, money+io, 697 lines
 4. ProductService.SearchProductsAsync()            risk 7.0  ← 0 tests, complexity 92,  money+io, 330 lines
 5. ImportManager.ImportOrdersFromXlsxAsync()       risk 7.0  ← 0 tests, complexity 86,  date+io,  319 lines
```

In about 30 seconds, across 4,061 methods, Pinion finds the spots a migration is most likely to break:
**2,185 high-risk methods with zero tests** — money/auth/date logic, a 697-line method at cyclomatic
complexity 174 — plus **2 WCF contracts** (a real .NET Framework→Core landmine) and **6 places that
hard-wire dependencies and need a seam introduced first**. The full ranking renders as an offline,
self-contained HTML dashboard (`analyze --format html`).

## 2. `generate` — lock the current behavior (deterministic, $0)

Pointed at the top high-risk methods, Pinion synthesized xUnit characterization tests, compiled and ran
them against the real code, and captured the actual output as golden masters:

```
Skipped 37 method(s) tagged io that may touch the filesystem/DB/network when run.
Done: 21/23 characterized (deterministic, $0).
```

**21 of 23** heavily dependency-injected service methods locked, with no AI and no cost. Where a service
injects collaborators it cannot construct, Pinion generates stub implementations so the method runs
against inert collaborators instead of dying on a null. Protected methods — a large share of the
substantial internal calculations here — are reached through a generated subclass that re-exposes them.

The 2 it could not lock were **reported, not silently dropped**: their generated tests failed to compile,
so they were isolated and named, and the rest of the batch still completed.

## 3. `verify` — prove behavior didn't change (the gate)

Re-running the locked suite against the current code:

```
BEHAVIOR VERIFICATION — nopScratch.csproj
Re-ran 21 locked method(s) against the current code.

✓ Behavior preserved: all 21 method(s) behave identically — safe to ship.   (exit 0)
```

That is the safety net in action. **Now migrate** — .NET Framework→Core, a refactor, an AI rewrite — and
re-run `verify`: any method whose behavior changed fails the build with a precise old-vs-new diff (console,
Markdown, or a self-contained HTML page). Exit code is non-zero on any change, so it drops straight into CI.

---

## What this does not show

Being specific about the limits, because a safety net you misjudge is worse than none.

**Two thirds of high-risk methods here were skipped, not locked.** 37 of the selected methods carry the
`io` tag, meaning running them could touch a filesystem, database or network. Pinion refuses to execute
those unless you pass `--allow-side-effects`. On a service layer that talks to a database, expect a large
fraction to be out of reach by default. `pinion seam` exists to chip away at this.

**Many recorded outcomes are exceptions rather than business results.** Across the 21 locked methods,
**66% of recorded outcomes are `NullReferenceException` or `ArgumentNullException`** (down from 72%
before 1.2.0, when stubs returned null and guard clauses fired immediately). The cause is depth: a stub
returns a constructed object, but the properties *on* that object are still null, and DI-heavy code
dereferences several layers down.

That is still real, locked behavior — if a migration changes how a method handles null, `verify` fails —
but it is weaker than pinning the arithmetic. Pinion is at its strongest on calculation, parsing,
validation and branching logic, and weakest on orchestration code whose behavior is mostly its
collaborators'. The bundled sample, which is closer to the former, scores **78.6% mutation** under
`pinion prove`.

## What this demonstrates

- The **readiness audit** works at scale on real, gnarly business code and surfaces genuinely scary,
  untested, money-touching methods.
- The **deterministic generator** locks real behavior on a heavily DI'd service layer, offline and free —
  no code leaves the machine, no API key, nothing to expense.
- The **verify gate** turns "we have tests" into "we can prove this migration changed nothing," with a
  CI-ready exit code and a shareable diff.

> Change everything, break nothing, prove it.

_(The HTML dashboard and verification page are generated locally by the commands above; they are not
committed here because they are large and derived from a third-party codebase.)_
