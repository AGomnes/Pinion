# Pinion

> **Migrate with AI. Prove it didn't break anything.**
>
> Pinion locks what your legacy .NET code *does today* into runnable tests, then tells you
> exactly what a migration changed. Free, and it runs entirely on your machine.

AI migration tools will happily rewrite your .NET Framework app. They validate the result by
checking that **your build and tests pass**, which proves almost nothing on the codebases that
most need migrating, because those are the ones without tests. That is why teams report spending
[hundreds of hours fixing failed partial upgrades](https://www.techzine.eu/news/devops/136571/copilot-replaces-net-upgrade-tool-developers-complain/).

.NET 8 and .NET 9 both reach
[end of support on 10 November 2026](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/),
so most of those migrations are happening now.

Pinion closes the gap in three commands:

```pwsh
pinion analyze  ./MyApp                  # where am I exposed? (free, offline, no AI)
pinion generate ./MyApp -p ./MyApp.Tests # lock what the code does TODAY as golden masters
#          … now migrate: Copilot, a contractor, or by hand …
pinion verify   ./MyApp                  # identical, or exactly what changed and where
```

Pinion **never decides what your code *should* do.** It records what it *actually does*, bugs
included, and freezes that. Because it measures rather than judges, it is safe to point at code
nobody on the team understands any more.

That practice is Michael Feathers' **characterization testing** (*pinning tests*, from *Working
Effectively with Legacy Code*), automated for .NET, in C# **and VB.NET**.

> **See it on real code → [PROOF.md](PROOF.md)**: a full `analyze → generate → verify` run on
> [nopCommerce](https://github.com/nopSolutions/nopCommerce) (4,072 methods). Finds 2,189 high-risk
> untested methods, locks 17 real service methods offline for $0, and proves behavior is unchanged.

### Why it runs on your machine

Microsoft has deprecated the free, local .NET Upgrade Assistant in favor of the GitHub Copilot
upgrade agent. It is a capable tool, but by design it
[cannot run offline](https://learn.microsoft.com/en-us/dotnet/core/porting/github-copilot-upgrade/faq):
it requires Copilot cloud infrastructure, and your source is the context it sends. On Copilot
Free/Pro/Pro+, interaction data including code snippets is
[used for model training by default](https://about.gitlab.com/blog/github-copilots-new-policy-for-ai-training-is-a-governance-wake-up-call/)
unless you opt out. Business and Enterprise are contractually exempt.

For regulated finance, defense, healthcare, and anything air-gapped, that is a blocker rather than
a preference. Those are also the shops still running the oldest .NET.

Pinion's default path makes **zero network calls**. There is no HTTP client on the deterministic
path at all, the source is public so you can check, and [TRUST.md](TRUST.md) documents egress
command by command. AI is opt-in (`--provider anthropic`, `openai`, or `azure-openai`), off by default,
and never required.

> **The name.** The spec's working title was *BehaviorLock*, but that name was taken. A **pinion**
> is the small gear that meshes with a larger one to drive it safely, and Feathers calls these
> "pinning tests."

---

## Status

**v1.0.0, published on [NuGet](https://www.nuget.org/packages/Pinion).** Every command is shipped.
236 tests run against .NET 8 and .NET 10 on each commit, and every release re-runs the end-to-end
pipeline that synthesizes, compiles, executes and snapshots a generated test.

`analyze` · `generate` · `verify` · `accept` · `prove` · `seam` · `ci` · `quickstart` · `init-tests`

C# and VB.NET. `generate` is deterministic and offline by default; AI providers are opt-in and
never required.

Validated beyond the bundled samples on NodaTime, Humanizer, eShopOnWeb and nopCommerce
(see [PROOF.md](PROOF.md)), plus real .NET Framework apps using WebForms, WCF and EF6.

**Known limits**

- The deterministic generator skips generic methods and generic containing types. Use
  `--provider anthropic` for those.
- VB.NET targets are deterministic-only; the AI context extractor reads C# syntax.
- Non-public methods are skipped, since a test in a separate assembly can only call public members.
- Deferred, and needing a live API key to finish: Batch API pricing and cheap/strong model routing.
  Both are cost optimizations for the opt-in AI path, so neither affects default use.

---

## Install

Pinion ships as a **.NET tool**:

```pwsh
dotnet tool install -g Pinion      # then `pinion` is on PATH
pinion analyze .                   # free migration-readiness audit
pinion quickstart <code.csproj>    # …or go straight to locking your riskiest behaviors
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
ANALYZE layer (the auditable core — zero network, no AI):
  Pinion.Engine          language-agnostic core — IR, risk scoring, domain tagging,
                         report building/rendering. Speaks ONLY the IR; NO Roslyn.
  Pinion.Adapters.CSharp the only place Roslyn lives for analyze. Produces the IR;
                         never leaks compiler types past the ILanguageAdapter boundary.

GENERATE layer (test synthesis):
  Pinion.Generate              LLM client, secret scrubber, prompts, orchestrator,
                               IGenerationAdapter. References the free engine.
  Pinion.Adapters.CSharp.Generate  C# context extraction + emit + compile/run/snapshot.

  Pinion.Cli                   composition root — bundles both layers behind one CLI.
```

**The layer boundary is enforced by the project graph — the dependency only ever flows
generate → analyze, never the reverse.** `Pinion.Engine` / `Pinion.Adapters.CSharp` have no
compile-time path to any generation code (`ILanguageAdapter` covers analyze only; `IGenerationAdapter`
lives in `Pinion.Generate`). This began as a free/paid split; with Pinion now source-available it
serves a better purpose: **the analyze core can be vendored, audited, or run standalone** without
pulling in the LLM client at all. If your security review only clears the offline half, that half is a
genuinely separate artifact — not a runtime flag you have to trust.

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
| `<path>` | — | `.sln` / `.csproj` / `.vbproj` / directory to scan (VB.NET routes to the VB adapter) |
| `-f, --format` | `console` | `console`, `markdown`, `json`, or `html` (the offline dashboard) |
| `-o, --out <file>` | — | also write the rendered report to a file |
| `--open` | off | open the rendered report in the browser (a static file — no server is started) |
| `--top <n>` | `0` (all) | show only the top N hotspots |
| `--threshold <d>` | `3.5` | risk at/above which an untested method is "high-risk" |
| `--coverage` | off | run the target's tests under Coverlet and include executed coverage % (slower) |
| `--mutation-report <f>` | — | overlay per-file mutation scores in the HTML report (from `prove --report-json`) |
| `--target-framework <tfm>` | — | also report framework APIs the code uses that do **not** exist on that target (e.g. `net10.0`). Resolved against that framework's reference assemblies — no catalog, no network |
| `--include-refs` | off | when the target is a single `.csproj`, also analyze its referenced projects (default: just the target project) |
| `-v, --verbose` | off | print Roslyn/MSBuild diagnostics to stderr |

### What breaks on the target — `--target-framework`

Before locking behavior, it helps to know what will not compile at all:

```pwsh
pinion analyze ./MyApp.csproj --target-framework net10.0
```

```
Migration landmines:      2 WCF
Unavailable on net10.0:  1 framework type(s)

APIS UNAVAILABLE ON net10.0
────────────────────────────────────────────────────────────
  System.Threading.Lock  (1 use(s))  first: Ledger.cs:7
────────────────────────────────────────────────────────────
These must be replaced before the migration compiles. Lock the behavior of the
methods that use them FIRST, so the replacement can be proved equivalent.
```

This is resolved against **the target framework's own reference assemblies**, which ship with every SDK
install. There is no curated list of removed APIs to go stale, no network call, and the answer is exact
for the version you are actually targeting.

Deliberately quiet when it cannot be sure: an uninstalled target reports nothing, and code that did not
resolve reports nothing rather than flagging your whole codebase. Legacy projects often will not restore
on a modern machine — the syntactic landmine detection above still covers those.

## `quickstart` — the one-command golden path

The fastest way from "installed" to "my riskiest behavior is locked": analyze the code, pick the
highest-risk untested public methods, **scaffold the host test project if you don't have one**, and
lock them as golden masters — one command, offline, no AI, $0.

```pwsh
pinion quickstart <code.csproj>              # lock the top 10 riskiest behaviors
pinion quickstart <code.csproj> --top 25     # go wider
pinion quickstart <code.csproj> --dry-run    # show what would be locked; write nothing
```

It skips `io`-tagged methods by default (running them would touch the filesystem/DB/network — pass
`--allow-side-effects` to include them), notes when money/auth-sensitive methods are characterized
(snapshots are secret-scrubbed; review before committing), and ends with the exact `verify` command
that proves your migration later. `--test-project` / `--tfm` are available when the defaults don't fit.

## `seam` — make untestable methods lockable

`analyze` flags methods that hard-wire ambient values (`needs seam: DateTime.Now`). `pinion seam`
**introduces the Feathers seam automatically**: the original signature becomes a delegating wrapper —
every existing caller compiles and behaves identically — and the real body moves to an overload whose
ambient values are parameters, which is deterministic and therefore lockable:

```csharp
public long TicksNow() => TicksNow(DateTime.Now);                       // wrapper: callers unchanged
public long TicksNow(global::System.DateTime now) => now.Ticks;        // overload: generate can lock it
```

```pwsh
pinion seam <path>                    # preview: per-method diffs, writes NOTHING
pinion seam <path> --target Invoice   # scope by method/type name
pinion seam <path> --apply            # write — compile-checked, and REVERTED if the build breaks
```

Handles `DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`, and `Guid.NewGuid()` — including
async methods, overrides, name collisions, and attributes; re-running is idempotent. Resource
obstacles (`File`, `HttpClient`, `SqlConnection`, …) need a *designed* abstraction, so they're
deliberately left flagged as manual rather than guessed at. Editing your source is the highest-trust
action in the product — hence preview-by-default and the build-gated, self-reverting apply.

## `generate`

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

`generate` writes its tests into a host **test project** that references the code under test plus xunit +
Verify. If you don't have one, scaffold it: **`pinion init-tests <code.csproj>`** emits a Verify-ready
xUnit project (and prints the exact `generate` command to run next).

```pwsh
# One-time: scaffold a host test project next to the code you want to characterize.
pinion init-tests samples/LegacyShop/src/LegacyShop/LegacyShop.csproj

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
| `<path>` | — | code to characterize (`.sln`/`.csproj`/`.vbproj`/dir; a VB path routes to the VB adapter) |
| `-p, --test-project` | — | test project to host generated tests (refs the code + xunit + VerifyXunit) |
| `-t, --target` | — | only methods whose name/id contains this substring |
| `--top <n>` | `1` | when no `--target`, take the top N high-risk untested methods |
| `--provider` | `deterministic` | `deterministic` (offline, no AI — default). Opt-in AI: `anthropic`, `openai`, `azure-openai`, each needing its own key env var. Also `heuristic` (offline pipeline test). VB.NET targets are `deterministic`-only |
| `--model` | per provider | `claude-sonnet-4-6` for `anthropic`, `gpt-4.1` for `openai`. For `azure-openai` this is your **deployment name** and is required |
| `--base-url` | provider’s own host | point at a local/self-hosted endpoint, a gateway, or your Azure OpenAI resource. Works for air-gapped runs against Ollama, vLLM, LM Studio, etc. |
| `--max-repairs` | `3` | compile/run repair attempts per target (AI path) |
| `--allow-side-effects` | off | include `io`/`money`-tagged methods (see safety note below) |
| `--exclude <pat>` | — | never run/send methods matching (substring or glob; repeatable; also reads `.pinionignore`) |
| `--no-send <pat>` | — | mark files/namespaces whose source must **never** be sent to the AI (repeatable; also reads `.pinionnosend`). Still locally characterizable with `--provider deterministic` |
| `--timeout <s>` | `180` | per-method run timeout — kills a hung/infinite-loop target |
| `--dry-run` | off | print the exact outbound bytes / synthesized test and run nothing |

### Non-determinism is caught, not baked in

A method that returns something different each run (a timestamp, a GUID, anything reading
`DateTime.Now`) would produce a golden master that fails every future `verify`. Pinion detects this at
capture time: after locking a snapshot it re-runs the test, and if the output differs the target is
**quarantined** rather than recorded. The test and its snapshot are deleted, and the report names the
ambient dependency responsible so `pinion seam` can make the method lockable.

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

## `verify` — did the migration change any behavior?

This is the payoff. Once `generate` has locked behavior as golden masters, run your migration
(.NET Framework→Core, a refactor, an AI rewrite — anything), then `pinion verify` re-runs the locked
suite against the **current** code and tells you exactly what changed. It's *proof*, not a prediction:
a method whose output still matches its golden master behaves identically; one that doesn't shows a
diff of old vs. new.

```pwsh
pinion verify <test.csproj>                                    # console report
pinion verify <test.csproj> --format markdown -o behavior.md   # shareable proof artifact
pinion verify <test.csproj> --format html -o behavior.html     # self-contained offline page (green/red diffs)
pinion verify <test.csproj> --format json                      # for CI tooling
```

Example after a migration that altered one method:

```
BEHAVIOR VERIFICATION — MyApp.Tests.csproj
Re-ran 61 locked method(s) against the current code.

✓ Identical: 60 of 61
⚠ CHANGED:   1 of 61   (− was locked  ·  + current code)

CHANGED METHODS
────────────────────────────────────────────────────────────
1. InvoiceService.CalculateVat
   {
     Input: (9999m, "DE", false),
   - Outcome: 1899.81
   + Outcome: 1900.00
   }
```

- **Exit code is the gate** — `0` only when every locked method behaves identically; non-zero on any
  change or a build break. Drop it into CI (or use `pinion ci`) and a behavior change fails the build.
- **Offline, no AI** — it just runs the committed tests and diffs the snapshots.
- If the suite no longer compiles, `verify` reports that the **migration broke the build** rather than
  guessing about behavior.

**Scope a PR check with `--since`.** On a big suite you don't re-run everything for a 3-line change:
`pinion verify <test.csproj> --since main` re-verifies only the behaviors the change could move —
methods in files changed since the git ref **plus their transitive callers** (from the call graph).
"Nothing affected" exits 0. It's a speed optimization for PR feedback; keep the full `verify` as the
authoritative pre-merge gate (static call graphs can't see reflection/DI dispatch).

**Triage with `accept`.** When `verify` shows changes, some are *intended* (you fixed a bug on
purpose). `pinion accept <test.csproj> --name <method>` re-baselines those — promotes the current
output to the golden master (scrubbed) — so the suite tracks the new expected behavior and what
remains is a real regression. It requires an explicit scope (`--name` or `--all`): accepting is
destructive, so it never happens by accident.

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
| + **conjunctive guard solver** (a witness + one near-miss per guard: length / char-class / affix) | —<sup>†</sup> | AuthHandler **58% → 81%** |
| + **regex guard solver** (synthesize a string that *matches* a `Regex.IsMatch` pattern, + a non-match) | —<sup>†</sup> | SkuValidator **75% → 100%** |
| + **constructed stubs** (a stubbed dependency returns an object, not `null`) + **protected-method probes** | **78.6%** | HardCases **46% → 87%**, SkuValidator **0% → 100%** |

<sup>†</sup> Measured method-scoped this pass (AuthHandler 57.69% → 80.77%; SkuValidator 75% → 100% — a small method, so a mechanism proof more than a big-sample showcase). Stryker 4.14, identical config before/after; the whole-sample overall wasn't re-run.

**The score depends heavily on how much you lock.** The 78.6% row is the whole sample with
`--top 80 --max-targets 80`. The identical generator on the identical code scores **60.4%** at
`--top 20`, because 49 mutants sit in methods that were never targeted rather than in methods the tests
failed to pin. `--max-targets` defaults to **25**, so a first run on a large codebase measures your
coverage choice more than it measures the generator. Raise it before drawing conclusions.

So the generator clears length/letter/digit guards, reaches `int.TryParse` success branches, pins the
behavior in `out`/`ref` parameters, **constructs parameter objects whose fields carry the mined branch
constants** (a `CartLine` with `Quantity: 10` and a `"CLEARANCE"` SKU mined from `Sku.StartsWith(...)`),
and — for numeric methods — adds a **property-based tier** that samples *every parameter at once* over
ranges derived from the mined constants. That joint sampling escapes the one-hot trap where a fixed base
value (`isExempt = true`, `daysLate = -1`) silently starves whole code paths, so arithmetic and loop
behavior finally get pinned. The samples are seeded from the method id and **baked into the test as
literals**, so the golden master stays reproducible and the generated test takes no runtime dependency on
CsCheck. The **conjunction-of-guards** case (e.g. `AuthHandler`: a token that must be 16+ chars AND have a
letter AND a digit AND not start with `-`) is now handled by a deterministic **guard solver** that emits a
witness clearing every guard plus one boundary near-miss per guard — taking AuthHandler from 58% to 81%
with no AI. A method guarded by **`Regex.IsMatch`** is handled the same way: the solver synthesizes a
string that *matches* the pattern (verified against `System.Text.RegularExpressions`, so a pattern it
can't model is simply skipped) — reaching the accept branch a random `"Ab1…"` never would. The remaining
tail (private helpers, multi-step state setup) is what `--provider anthropic` is for. The point: every
change is **measured**, not guessed, and `prove` tells you per method where the net is strong vs thin.

## `ci` — hang the net in the doorway

A behavior net only catches a regression if it runs on every change. `pinion ci` scaffolds a CI
workflow whose core is the **behavior gate**: the committed characterization tests run as `dotnet test`
and **fail the PR when a migration (framework upgrade, refactor, dependency bump) alters observable
behavior**. It also wires in the free `analyze` risk report (commented, opt-in) and, with `--with-prove`,
the mutation-score step.

```pwsh
pinion ci -p tests/MyApp.Tests.csproj                 # → .github/workflows/pinion.yml
pinion ci --provider azure -p tests/MyApp.Tests.csproj # → azure-pipelines-pinion.yml
pinion ci -p tests/MyApp.Tests.csproj --with-prove     # also post the mutation score
pinion ci --stdout                                     # preview without writing
```

Supports **GitHub Actions** and **Azure DevOps** (legacy .NET shops are heavily Azure DevOps/TFS). It's
offline (it only emits text), refuses to clobber an existing file without `--force`, and emits an
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

### License — free for everyone, permanently

**Pinion is free. For everyone. There is no paid version, no tier, no seat count, no key, no account,
no activation, and nothing phones home.** A bank, a consultancy, a student, a government department,
a competitor: all free, forever, including commercial and internal use on proprietary code.

That is the whole story for anyone who wants to *use* Pinion. The rest of this section only matters if
you want to *sell* it.

Licensed under the [Functional Source License 1.1 with an Apache-2.0 future
grant](LICENSE.md) (`FSL-1.1-ALv2`).

**You may** use Pinion for any purpose, commercially or not; run it on proprietary code; use it on paid
client engagements as a consultant; read, modify, fork, and redistribute it.

**You may not** sell Pinion, host it as a paid service, or ship it inside a product that substitutes for
it. That single carve-out, "Competing Use", is the only right withheld — and it is aimed at one thing:
stopping someone wrapping Pinion in a SaaS and charging for it. It restricts vendors, not users.

**Two years after each release, that version becomes Apache-2.0 automatically**, irrevocably, with no
restriction at all. The clock runs per version: 1.2.0 becomes Apache-2.0 in August 2028. The
unrestricted grant is already given; it is only deferred.

> **On the words "open source".** Pinion is *source-available*, not OSI-approved open source. The Open
> Source Definition forbids restricting fields of endeavor, and the Competing Use clause does exactly
> that, so calling it "open source" would be inaccurate. All of the source is public and auditable.

MIT was rejected because the restriction costs legitimate users nothing: the enterprise locking its own
legacy behavior, the consultancy migrating a client, and the developer reading the code to check it
doesn't phone home are all fully permitted. Only repackaging Pinion and selling it is excluded.

A non-commercial license was rejected because the codebases that need characterization tests are
commercial ones. Such a clause would exclude essentially every intended user, and "commercial use" is
vague enough that enterprise legal teams default to *no*. Internal business use is granted explicitly
for that reason.

<details>
<summary>Dormant: the offline license gate</summary>

The repo still contains an offline, signed license gate (`LicenseGate`, ECDSA P-256, `pinion license
activate|verify|machine-id`, and a vendor-only `tools/Pinion.LicenseAdmin` issuer). **It gates nothing
today** and no subscription is sold. It is retained, unenforced, only so a separately negotiated
commercial agreement remains possible later without rebuilding it.

It verifies entirely offline, with no activation server, because an online check would contradict the
local-first promise. The product embeds only the issuer **public** key and can solely verify; the
private key and all minting code live in a tool that never ships. The archived commercial EULA draft is
at [docs/EULA-draft-superseded.md](docs/EULA-draft-superseded.md) and governs nothing.
</details>


**Security.** Nothing leaves your machine unless you opt in with an AI provider (`--provider anthropic`,
`openai`, or `azure-openai`), none of which is the default. Without such a flag Pinion makes no network
request at all. With one, the only thing that leaves is per-method context, through a **single,
auditable** HTTP call to that one provider. A pre-send scrubber strips
keys/passwords/tokens/connection-string credentials; a never-send allowlist (`--no-send` /
`.pinionnosend`) keeps designated files/namespaces fully offline; `--dry-run` shows the literal
payload; no telemetry. Use Anthropic Zero-Data-Retention (an org-level account setting), or
`--base-url` to run fully offline against a local model.

📄 **Full, code-accurate data-handling statement: [TRUST.md](TRUST.md)** — what does and doesn't
leave the machine per command, how to audit the single outbound call yourself, air-gapped mode, and a
[DPA template](docs/DPA-template.md) for service engagements.

### API-key handling & cost controls

**Key hygiene.** The API key is read **only** from the `ANTHROPIC_API_KEY` environment variable —
never a CLI flag (flags leak into shell history and process listings). It travels solely as the
`x-api-key` request header, so it is never in the request body, the `--dry-run` output, or verbose
logs; error messages log response bodies only. The pre-send scrubber also redacts `sk-ant-…` tokens
from outbound source, so a key embedded in code won't reach the model either.

**Setting the key (end users with the installed tool).** The key lives nowhere inside Pinion — not in
the binary, not in a shipped config file — so you never need access to the source to use an AI provider.
It is a runtime environment variable you set in your own environment. Get a key from the
[Anthropic Console](https://console.anthropic.com/) (*API Keys*), then set `ANTHROPIC_API_KEY` and run
`pinion generate … --provider anthropic` in the same environment:

```pwsh
# Windows (PowerShell) — current session only:
$env:ANTHROPIC_API_KEY = "sk-ant-…"

# Windows — persist for your user (then open a NEW terminal so it's picked up):
setx ANTHROPIC_API_KEY "sk-ant-…"
```

```bash
# macOS / Linux — current shell (append the same line to ~/.zshrc or ~/.bashrc to persist):
export ANTHROPIC_API_KEY="sk-ant-…"
```

In CI, store it as a **secret** (never in the YAML) and expose it as an env var on the step:

```yaml
# GitHub Actions
- run: dotnet pinion generate … --provider anthropic
  env:
    ANTHROPIC_API_KEY: ${{ secrets.ANTHROPIC_API_KEY }}

# Azure DevOps — map a secret pipeline variable to the env:
- script: dotnet pinion generate … --provider anthropic
  env:
    ANTHROPIC_API_KEY: $(ANTHROPIC_API_KEY)
```

The customer brings their own key; the vendor issues only the offline **license** (separate — see
[License](#license--free-for-everyone-permanently)). The **local-model** path (`--base-url
http://localhost:<port>`) reads the *same* variable — most local Anthropic-compatible servers accept
any non-empty value (or a token you configure), so set `ANTHROPIC_API_KEY` to that and nothing leaves
the machine.

Two things worth knowing about where the key lives:
- **`setx` (and the GUI) persist the key** to your user environment — on Windows, the registry — so it
  survives reboots. Prefer the session-only `$env:` / `export` form if you'd rather it not be stored.
- **Pinion strips these key variables from the environment of every child process it launches** —
  notably the `dotnet test` it runs to *execute your code* — so a method under characterization can't
  read the API key out of its own process environment and exfiltrate it
  ([`ProcessRunner.ScrubSecretsFrom`](src/Pinion.Adapters.CSharp/ProcessRunner.cs)).

**Runaway-spend guards** (a loop over methods × repair attempts hits a paid API):

| Guard | Default | Effect |
|---|---|---|
| `--max-spend <usd>` | `5.00` | Hard ceiling — the run stops before the next call once cumulative estimated cost crosses it. |
| `--max-targets <n>` | `25` | Caps how many methods one run characterizes (stops an accidental broad `--target`/`--top`). |
| `--max-repairs <n>` | `3` | Bounds calls per target (≤ N+1). |
| auth errors | — | A `401/403` aborts the run immediately instead of burning more calls. |
| rate-limit / 5xx | — | A `429`/`5xx` is retried with exponential backoff (honoring `Retry-After`); the run aborts only if retries are exhausted. |

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
src/Pinion.Engine                  ANALYZE: IR, risk scoring, domain tagging, report renderers
src/Pinion.Adapters.CSharp         ANALYZE: Roslyn analyze — call graph, landmines, seams, coverage
src/Pinion.Adapters.VisualBasic    ANALYZE: VB.NET analyze adapter + solution loading (MSBuild + source-scan)
src/Pinion.Generate                GENERATE: LLM client, scrubber, prompts, orchestrator, licensing
src/Pinion.Adapters.CSharp.Generate GENERATE: C# + VB extract + emit + compile/run/snapshot (tests are always C#)
src/Pinion.Cli                     `pinion` global tool (composition root over both layers)
tests/Pinion.Tests                 engine, analyze, landmine, generation, seam, and licensing tests (230+)
samples/LegacyShop           a deliberately under-tested sample (risk + blast radius + coverage)
samples/LegacyVb             the VB.NET analog (analyze + generate: mined Select Case constants)
samples/LegacyWeb            legacy WebForms/WCF/EF6 fixtures (landmine detection)
samples/LegacyFramework      classic non-SDK v4.8 .csproj (source-scan fallback)
CHANGELOG.md                 release history (Keep a Changelog)
TRUST.md                     code-accurate data-handling statement (what leaves the machine, per command)
```
