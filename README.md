# Pinion

> Lock the current behavior of a legacy .NET codebase so you can migrate or refactor
> **without breaking the base — and prove it.**

Pinion is a .NET CLI tool that productizes Michael Feathers' **characterization testing**
(a.k.a. *pinning tests*): it measures where a codebase is exposed, and — later — freezes
what the code *currently does* as a golden-master safety net. Change everything, break
nothing, prove it.

> **The name.** The working title in the spec was *BehaviorLock*, but that name is already
> taken by a near-identical open-source project in the same space. **Pinion** is free on
> NuGet and on-theme: Feathers calls characterization tests "pinning tests," and a pinion
> is the small gear that meshes with a larger one to drive it safely.

---

## Status

| Milestone | Scope | State |
|---|---|---|
| **1 — `analyze` skeleton** | Roslyn load, IR, cyclomatic complexity, test detection, ranked report (console/Markdown/JSON) | ✅ done |
| **2 — enrich `analyze`** | call graph (blast radius), domain tagging, migration-landmine detection, Coverlet coverage % | ✅ done |
| **3 — `generate` (AI)** | context extraction, thin Anthropic client, secret scrubber + `--dry-run`, compile→run→repair, golden masters | ✅ done |
| 4 — hardening / premium | mutation testing (`prove`) ✓, property-based (CsCheck) ✓, CI templates (`ci`) ✓, HTML dashboard ✓; prompt caching + Batch API deferred (AI) | non-AI items done |

The free, AI-free **`analyze`** command is complete. The paid **`generate`** command (Milestone 3)
writes characterization tests that lock current behavior, with a compile→run→repair loop.

---

## Install

Pinion ships as a **.NET tool**:

```pwsh
dotnet tool install -g Pinion      # then `pinion` is on PATH
pinion analyze .
```

On a **locked-down machine** (some enterprise Windows policies block unsigned `.exe` shims), install it
as a *local* tool and invoke it through the runtime, which bypasses the shim:

```pwsh
dotnet new tool-manifest
dotnet tool install Pinion
dotnet pinion analyze .            # runs via the dotnet muxer, no shim
```

Building the package from source: `dotnet pack src/Pinion.Cli -c Release -o ./artifacts`.

---

## Architecture

The product is split so other languages can be added later as thin adapters:

```
FREE tier (open-source engine — analyze):
  Pinion.Engine          language-agnostic core — IR, risk scoring, domain tagging,
                         report building/rendering. Speaks ONLY the IR; NO Roslyn.
  Pinion.Adapters.CSharp the only place Roslyn lives for analyze. Produces the IR;
                         never leaks compiler types past the ILanguageAdapter boundary.

PAID tier (generate — AI):
  Pinion.Generate              LLM client, secret scrubber, prompts, orchestrator,
                               IGenerationAdapter. References the free engine.
  Pinion.Adapters.CSharp.Generate  C# context extraction + emit + compile/run/snapshot.

  Pinion.Cli                   composition root — bundles both tiers behind one CLI.
```

**The free/paid boundary is enforced by the project graph — the dependency only ever flows
paid → free, never the reverse.** The free `Pinion.Engine` / `Pinion.Adapters.CSharp` have no
compile-time path to any paid code (the free `ILanguageAdapter` covers analyze only; the paid
`IGenerationAdapter` lives in `Pinion.Generate`). So the free engine can be open-sourced and
shipped standalone, and the free tier literally cannot reach the paid generate code. (A
runtime license/entitlement gate on `generate` is a separate, deferred business decision.)

The boundary is [`ILanguageAdapter`](src/Pinion.Engine/Abstractions/ILanguageAdapter.cs);
the shared vocabulary is the IR [`CodeUnit`](src/Pinion.Engine/Model/CodeUnit.cs). Get a new
language working = write a new adapter, not a rewrite.

### Transparent risk score (no ML)

Risk is a weighted, fully itemized sum so every ranking is auditable
([`RiskScoring.cs`](src/Pinion.Engine/Analysis/RiskScoring.cs)):

```
risk = w·complexity + w·untested + w·domain + w·blastRadius + w·size + w·landmine
```

Each report row shows *why* it ranked where it did. Weights and thresholds are configurable.

---

## Build & run

Targets **net8.0** (the migration audience's destination) and also **net10.0** so it builds
and runs on dev boxes that only have the .NET 9/10 SDK.

```pwsh
dotnet build Pinion.slnx
dotnet test  tests/Pinion.Tests/Pinion.Tests.csproj
```

### Install as a global tool

```pwsh
dotnet pack src/Pinion.Cli/Pinion.Cli.csproj -c Release -o artifacts
dotnet tool install -g Pinion --add-source artifacts
pinion analyze <path>
```

The package targets both `net8.0` and `net10.0`; `dotnet tool install` picks the one matching
your runtime. (On a host whose security policy blocks launching freshly-created apphost `.exe`s,
the generated `pinion` shim may be denied — run the payload via the muxer instead:
`dotnet <tool-store>/.../pinion.dll analyze <path>`.)

Run `analyze` against the bundled sample (or any .sln/.csproj/directory):

```pwsh
# If you have the .NET 8 SDK/runtime:
dotnet run --project src/Pinion.Cli -- analyze samples/LegacyShop/LegacyShop.slnx

# On a box without the .NET 8 runtime, run the net10.0 build directly:
dotnet src/Pinion.Cli/bin/Debug/net10.0/pinion.dll analyze samples/LegacyShop/LegacyShop.slnx
```

> Restore the target solution first (`dotnet restore <target>`) so Roslyn can resolve
> references and detect test projects accurately.
>
> **Legacy / unrestorable code still works.** If MSBuild can't load the target — a classic
> non-SDK `.csproj`, a missing/broken MSBuild, unrestored references — Pinion falls back to
> scanning the `.cs` files directly and still produces a report. The analysis is
> syntactic + source-level semantic, so it doesn't require a successful build. See
> `samples/LegacyFramework` (a real `v4.8` non-SDK project).

### `analyze` options

| Option | Default | Meaning |
|---|---|---|
| `<path>` | — | `.sln` / `.csproj` / directory to scan |
| `-f, --format` | `console` | `console`, `markdown`, or `json` |
| `-o, --out <file>` | — | also write the rendered report to a file |
| `--top <n>` | `0` (all) | show only the top N hotspots |
| `--threshold <d>` | `3.5` | risk at/above which an untested method is "high-risk" |
| `--coverage` | off | run the target's tests under Coverlet and include executed coverage % (slower) |
| `-v, --verbose` | off | print Roslyn/MSBuild diagnostics to stderr |

## `generate` (paid AI tier)

Writes xUnit + [Verify](https://github.com/VerifyTests/Verify) characterization tests for chosen
targets, then **compiles, runs, and captures the actual output as a golden master**. The key
inversion: it does **not** assert what the code *should* return — it records what it *does*
(bugs included). A later migration that changes behavior fails the snapshot diff.

**Deterministic by default; AI is opt-in.** The default generator is **AI-free**: it reads the
real types from Roslyn's semantic model and constructs concrete arguments (building objects and
collections — e.g. a `CartLine` list, not `null`), then emits the test. Crucially, it **mines the
constants the method branches on** — switch-case labels, comparison thresholds — and uses them plus
boundary neighbours as inputs, so it reaches real branches rather than recording one shallow path.
(For `CalculateVat` it derives `"NO"/"UK"/"DE"/"FR"` and `9999/10000/10001` automatically.) It is
**offline, free, and reproducible** — identical source in → identical test + identical golden
master, so re-runs never produce phantom diffs, and **nothing leaves the machine**. AI is an
explicit upgrade for the harder cases (inputs that must satisfy a *conjunction* of guards, or
multi-step state setup).

It handles real-world C#: **async** (`Task`/`Task<T>`/`ValueTask<T>` are awaited), **`out`/`ref`**
parameters (`out _` / ref temps), **overloads** (collision-safe class names), and **ref-struct
returns** (`Span<T>` captured as text). Generic methods/types are **skipped cleanly** (reported, not
emitted broken) — use `--provider anthropic` for those.

```pwsh
# Default — deterministic, offline, $0, nothing leaves the machine:
pinion generate samples/LegacyShop/LegacyShop.slnx `
  --test-project samples/LegacyShop/tests/LegacyShop.Tests/LegacyShop.Tests.csproj `
  --target ApplyDiscounts

# See the test it would write, without running anything:
pinion generate <path> --target ApplyDiscounts --dry-run

# Opt in to AI for the hard cases (needs ANTHROPIC_API_KEY + credits):
$env:ANTHROPIC_API_KEY = "sk-ant-…"
pinion generate <path> -p <test.csproj> --target CalculateVat --provider anthropic
```

Pipelines:
- **Deterministic (default):** semantic synthesis → emit → compile → run → snapshot. **Batched** —
  for N targets it emits all tests then builds/runs **once** (not N times); a test that doesn't
  compile is isolated and reported without failing the batch, and each test has a per-test timeout so
  one hang can't wedge the run. (Generic, private, and unconstructible methods are skipped cleanly.)
  No network, no repair loop (the code is valid by construction).
- **AI (`--provider anthropic`):** extract context (Roslyn) → **scrub secrets** → LLM → emit → compile → run → on failure feed errors back and repair (loop) → snapshot.

The **compile→run→repair loop** is verified two ways: unit tests drive it with a scripted
adapter (fail → repair → success, and give-up-after-max), and `--provider heuristic-faulty`
proves it in the *real* pipeline — it corrupts the first attempt so a real `dotnet test` build
fails, then the loop feeds the compiler error back and recovers on the next attempt:

```pwsh
pinion generate <path> -p <test.csproj> --target <m> --provider heuristic-faulty --verbose
#   attempt 1/4 … failed (1 diagnostic)   ← injected compile error, caught by a real build
#   attempt 2/4 … golden master captured  ← error fed back, recovered
```

This is the same path a live model hits whenever it emits code that doesn't build — the only
difference is where the text comes from.

| Option | Default | Meaning |
|---|---|---|
| `<path>` | — | code to characterize (`.sln`/`.csproj`/dir) |
| `-p, --test-project` | — | test project to host generated tests (refs the code + xunit + VerifyXunit) |
| `-t, --target` | — | only methods whose name/id contains this substring |
| `--top <n>` | `1` | when no `--target`, take the top N high-risk untested methods |
| `--provider` | `deterministic` | `deterministic` (offline, no AI — default) or `anthropic` (AI, opt-in, needs `ANTHROPIC_API_KEY`) |
| `--model` | `claude-sonnet-4-6` | generation model (Sonnet for reasoning; Haiku is the cheap tier) |
| `--base-url` | api.anthropic.com | override for a local Anthropic-compatible endpoint (air-gapped mode) |
| `--max-repairs` | `3` | compile/run repair attempts per target (AI path) |
| `--allow-side-effects` | off | include `io`/`money`-tagged methods (see safety note below) |
| `--exclude <pat>` | — | never run/send methods matching (substring or glob; repeatable; also reads `.pinionignore`) |
| `--timeout <s>` | `180` | per-method run timeout — kills a hung/infinite-loop target |
| `--dry-run` | off | print the exact outbound bytes / synthesized test and run nothing |

### Safety when running generated code

Generating a test **executes the target method** with synthesized inputs. For a pure function that's
harmless, but a method that touches the filesystem, a database, the network, or money could cause
**real side effects** when characterized (delete data, send an email, charge a card). Pinion guards this:

- **Side-effecting methods are skipped by default.** Any method tagged `io` or `money` is excluded
  unless you pass `--allow-side-effects`. (`analyze`'s domain tagging drives this.)
- **Exclude anything** with `--exclude <substring|glob>` (repeatable) or a `.pinionignore` file
  (one pattern per line) — excluded methods are never run and never sent to the AI.
- **Hard run timeout** (`--timeout`, default 180s) kills a build/test that hangs or loops forever.
- **Snapshots are scrubbed** — the captured golden master is run through the secret scrubber before
  it's written, since a return value can contain real secrets/PII and the file is committed.
- Generated files are written only under `<test-project>/PinionCharacterization/` (name-sanitized).

## `prove` — do the tests actually catch regressions?

A characterization test only has value if it **fails when behavior changes**. `pinion prove` runs
[Stryker.NET](https://stryker-mutator.io/) mutation testing against the code under a test project and
reports the **mutation score** — the percent of behavior-changing mutations the tests kill. This turns
"we generated tests" into the measurable claim "the tests catch N% of regressions".

```pwsh
dotnet tool install -g dotnet-stryker     # one-time (Pinion shells out to it)
pinion prove -p <test.csproj>
```

```
MUTATION SCORE: 74.4%  (killed 87/117; survived 19, uncovered 11)
PriceEngine.cs  76%   InvoiceService.cs  77%   AuthHandler.cs  62%   HardCases.cs  88%
```

**What the numbers honestly say**, and the measurable flywheel `prove` enables (all on the bundled
sample's *deterministic* tests, **no AI**):

| Improvement (each measured by `prove`) | Overall | Notable per-method |
|---|---|---|
| baseline deterministic | 42% | AuthHandler 15%, InvoiceService 33% |
| + **guard-clearing inputs** (derive a `Ab1Ab1…` string from the method's length constants) | 52% | AuthHandler **15% → 58%** |
| + **out/ref output capture** + numeric-string inputs | 56% | HardCases **63% → 81%** |
| + **object-field synthesis** (build parameter objects from mined constants) + predicate-arg mining | 63% | PriceEngine **53% → 76%** |
| + **property-based sampling** (CsCheck: deterministic joint-random rows for numeric methods) | **74%** | InvoiceService **37% → 77%** |

So the generator clears length/letter/digit guards, reaches `int.TryParse` success branches, pins the
behaviour in `out`/`ref` parameters, **constructs parameter objects whose fields carry the mined branch
constants** (a `CartLine` with `Quantity: 10` and a `"CLEARANCE"` SKU mined from `Sku.StartsWith(...)`),
and — for numeric methods — adds a **property-based tier** that samples *every parameter at once* over
ranges derived from the mined constants. That joint sampling escapes the one-hot trap where a fixed base
value (`isExempt = true`, `daysLate = -1`) silently starves whole code paths, so arithmetic and loop
behaviour finally get pinned. The samples are seeded from the method id and **baked into the test as
literals**, so the golden master stays reproducible and the generated test takes no runtime dependency on
CsCheck. The remaining gaps (e.g. `AuthHandler` conjunction-of-guards) are what `--provider anthropic` is
for. The point: every change is **measured**, not guessed, and `prove` tells you per method where the net
is strong vs thin.

## `ci` — hang the net in the doorway

A behavior net only catches a regression if it runs on every change. `pinion ci` scaffolds a CI
workflow whose core is the **behavior gate**: the committed characterization tests run as `dotnet test`
and **fail the PR when a migration (framework upgrade, refactor, dependency bump) alters observable
behavior**. It also wires in the free `analyze` risk report (commented, opt-in) and, with `--with-prove`,
the paid mutation-score step.

```pwsh
pinion ci -p tests/MyApp.Tests.csproj                 # → .github/workflows/pinion.yml
pinion ci --provider azure -p tests/MyApp.Tests.csproj # → azure-pipelines-pinion.yml
pinion ci -p tests/MyApp.Tests.csproj --with-prove     # also post the mutation score (needs a license)
pinion ci --stdout                                     # preview without writing
```

Supports **GitHub Actions** and **Azure DevOps** (legacy .NET shops are heavily Azure DevOps/TFS). It's
free-tier (it only emits text), refuses to clobber an existing file without `--force`, and emits an
editable placeholder if you don't pass `--test-project`.

## The HTML dashboard — `analyze --format html`

`analyze --format html` renders a **single self-contained "command center" dashboard**: headline metric
cards (mutation score, behavior coverage, high-risk count, landmines, estimated effort) over one
sortable/filterable table of methods — risk bars, domain/landmine chips, test status — with a
click-to-expand breakdown of *exactly* what drove each risk score. It also:

- **Groups by file** (toggle) — collapsible file sections, each with its own mutation score, so you can
  see *which file* is risky, not just which method.
- **Hands you the next step** — expanding an untested method shows the exact `pinion generate … --target`
  command to lock it, with a one-click **Copy** button. Diagnosis → action.
- **Calls out landmines** — when `analyze` finds WebForms/WCF/EF6 hazards, a banner surfaces them (they
  block a clean framework upgrade), not just a chip.

```pwsh
pinion analyze . --format html --out report.html              # risk dashboard
pinion prove -p tests/MyApp.Tests.csproj --report-json prove.json
pinion analyze . --format html --mutation-report prove.json --out report.html   # + per-file scores
```

Everything — CSS, [Alpine.js](https://alpinejs.dev) (MIT, vendored), and the data — is **inlined into
the one file**, which therefore **makes zero network requests**: it opens offline by double-click,
forever, and never leaks the analyzed code's shape to a CDN. No build step, no npm, nothing to audit —
the same discipline as the rest of Pinion. Dark/light follows your OS.

### Licensing (offline, no phone-home)

`generate` is gated by a **signed license verified entirely offline** (ECDSA P-256, BCL only) —
no activation server, because an online check would contradict the "code stays local" promise.
The product embeds the issuer **public** key and can ONLY verify; the **private** key and all
minting code live in a separate vendor-only tool that never ships.

```pwsh
# Customer side (shipped product — verify only, fully offline):
pinion license verify                 # check the current license
pinion license machine-id             # this machine's fingerprint — send it to the vendor
pinion generate … --dry-run           # preview is free, no license required

# Vendor side (separate tools/Pinion.LicenseAdmin — needs the secret signing key):
dotnet run --project tools/Pinion.LicenseAdmin -- keygen        # one-time: make a keypair
$env:PINION_SIGNING_KEY = "<private>"
dotnet run --project tools/Pinion.LicenseAdmin -- issue --subject "Acme Corp" --days 365 --machine <id>
```

A license is supplied via `--license`, `PINION_LICENSE`, or a `pinion.license` file. Verification
works air-gapped. Rejected: tampered, forged (wrong key), expired, **or bound to another machine**.

**Hardening vs. honest limits.** Two measures raise the bar against the realistic bypasses:
- **Node-locking** — a license can be bound to a machine fingerprint (`--machine <id>`), so a
  customer can't share their token; it only verifies on that machine.
- **No minting in the product** — issuance code (`keygen`/`issue`) is a separate vendor tool, so
  customer binaries contain nothing that can mint a license.

What it can't stop (true of *any* offline-licensed local software): someone who controls the
binary can edit the source and recompile, decompile/patch the DLL, or swap the embedded public
key. Hard enforcement would require running generation server-side — which would break the
local-first promise. The gate stops casual bypass, forgery, file-editing, and token-sharing; the
real moat is the hosted service, updates, and support.

**Security (the sales pitch):** code stays local; the only thing that ever leaves is per-method
context, through a **single, auditable** HTTP call to one endpoint. A pre-send scrubber strips
keys/passwords/tokens/connection-string credentials; `--dry-run` shows the literal payload; no
telemetry. Use Anthropic Zero-Data-Retention (an org-level account setting), or `--base-url` to
run fully offline against a local model.

### API-key handling & cost controls

**Key hygiene.** The API key is read **only** from the `ANTHROPIC_API_KEY` environment variable —
never a CLI flag (flags leak into shell history and process listings). It travels solely as the
`x-api-key` request header, so it is never in the request body, the `--dry-run` output, or verbose
logs; error messages log response bodies only. The pre-send scrubber also redacts `sk-ant-…` tokens
from outbound source, so a key embedded in code won't reach the model either.

**Runaway-spend guards** (a loop over methods × repair attempts hits a paid API):

| Guard | Default | Effect |
|---|---|---|
| `--max-spend <usd>` | `5.00` | Hard ceiling — the run stops before the next call once cumulative estimated cost crosses it. |
| `--max-targets <n>` | `25` | Caps how many methods one run characterizes (stops an accidental broad `--target`/`--top`). |
| `--max-repairs <n>` | `3` | Bounds calls per target (≤ N+1). |
| auth/quota errors | — | A `401/403/429` aborts the run immediately instead of burning more calls. |

Every run prints a usage summary (`N calls, X in / Y out tokens, ~$Z`). Offline providers
(`heuristic`, `--dry-run`, `--base-url` local model) cost **$0** and never trip the ceiling.

### Example output

```
MIGRATION READINESS REPORT — LegacyShop.slnx
Scanned: 8 methods across 5 files

Behavior coverage:        25% (2 of 8 methods tested)
Executed coverage:        2% lines, 0% branches (Coverlet)   # with --coverage
High-risk & UNPROTECTED:  5 methods
Migration landmines:      none detected

TOP RISK HOTSPOTS
────────────────────────────────────────────────────────────
 1. PriceEngine.ApplyDiscounts()    risk  4.9  ← 0 tests, complexity 14, money, 38 lines, 1 callers
 2. InvoiceService.CalculateVat()   risk  4.5  ← 0 tests, complexity 10, money, 29 lines, 1 callers
 3. AuthHandler.ValidateToken()     risk  4.4  ← 0 tests, complexity 10, auth, 18 lines, 1 callers
 ...
────────────────────────────────────────────────────────────
Estimated behavior-lock effort: 5 methods → ~1 day
```

Every hotspot exposes *why* it ranked there — domain tags (`money`, `auth`, …), blast radius
(`N callers`), complexity, size, and migration landmines — and the same breakdown is itemized
in the JSON. The bundled `samples/LegacyWeb` shows landmine detection
(`2 WCF, 1 WebForms, 1 EF6`); notably it doesn't even compile, yet detection still works
because the landmine/risk passes are **syntactic** — important for real legacy code whose
references won't restore on a modern box.

---

## Repo layout

```
src/Pinion.Engine                  FREE: IR, risk scoring, domain tagging, report renderers
src/Pinion.Adapters.CSharp         FREE: Roslyn analyze — call graph, landmines, coverage
src/Pinion.Generate                PAID: LLM client, scrubber, prompts, orchestrator
src/Pinion.Adapters.CSharp.Generate PAID: C# extract + emit + compile/run/snapshot
src/Pinion.Cli                     `pinion` global tool (bundles both tiers)
tests/Pinion.Tests                 engine + complexity + tagger + landmine + generation tests (31)
samples/LegacyShop           a deliberately under-tested sample (risk + blast radius + coverage)
samples/LegacyWeb            legacy WebForms/WCF/EF6 fixtures (landmine detection)
samples/LegacyFramework      classic non-SDK v4.8 .csproj (source-scan fallback)
PINION_SPEC.md               the source-of-truth build brief
```
