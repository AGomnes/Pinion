# Changelog

All notable changes to Pinion are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project follows
[Semantic Versioning](https://semver.org/) from its first published release.

The **generated test/snapshot format** is versioned separately and stamped into every generated test
(`// pinion-format: N`); a bump there is called out under the release that changes it.

## [Unreleased]

### Fixed
- Two shapes produced generated tests that did not compile, both found by running the full pipeline on
  nopCommerce and both invisible to the sample suite because nothing exercised them:
  - An array parameter whose candidate value is `null` emitted `new[] { null }`, which has no best type
    (CS0826/CS1503). Arrays are now explicitly typed.
  - A delegate parameter was built through a constructor. Delegates expose a compiler-provided
    `(object, IntPtr)` constructor that Roslyn reports as a real instance constructor, so the generator
    emitted `new Func<…>(default(object)!, default(nint)!)` and failed with CS0149. Delegates now become
    lambdas of the right arity returning a default result.

  With both fixed, nopCommerce goes from 21/23 to **23/23 characterized, zero failures**.

## [1.2.0] - 2026-08-25

### Added
- **Protected methods are now characterizable.** A test in another assembly cannot call a protected
  member, so the generator previously refused them. It now derives a probe subclass inside the test
  that re-exposes the method publicly and forwards to `base`, keeping the original name and signature
  (ref/out modifiers included) so every call site downstream is unchanged. Applies to non-sealed,
  non-abstract classes with a constructor reachable from a derived type; abstract and static members
  still skip.

  Measured on nopCommerce's top 150 high-risk untested methods: synthesis went from **14/25 to
  25/25**, with protected members having been ~45% of every attempted failure. They are the substantial
  internal calculations (`OrderTotalCalculationService.UpdateTaxRatesAsync`,
  `ImportManager.PrepareImportProductDataAsync`), not trivia.

### Changed
- **Generated interface stubs now return a constructed instance instead of `null`** when the return
  type has an accessible parameterless constructor. Stub-driven characterization was near-worthless on
  real service layers: measured across nopCommerce's locked methods, **72% of every recorded outcome
  was `ArgumentNullException` or `NullReferenceException`**, because a stubbed dependency returned null
  and the method's own guard clause threw before reaching any logic. That pinned the guard rather than
  the behavior, so a migration could rewrite the logic without failing a test. Re-measured on a fuller run of
  21 locked methods: `ArgumentNullException` outcomes fell from **103 to 28** and null-related outcomes
  from **72% to 66%**. (An earlier draft of this entry claimed zero ANEs and 53%; that came from a
  9-method subset and does not hold across the wider set. Corrected here rather than left standing.)
  Deliberately narrow: parameterless constructors only, since feeding arguments risks recursing through
  object graphs and invoking constructors with side effects. Regenerating existing tests will produce
  different (more meaningful) golden masters.

  The remaining NullReferenceExceptions have a separate cause: properties ON the constructed object are
  still null. Populating those means fabricating domain state, so it is not obviously correct.

### Fixed
- **Pinion could not lock a .NET Framework project**, which is its main use case: the whole premise is
  characterizing behavior BEFORE a migration, so the host test project is usually still net4x. Two
  defaults made that impossible, both surfacing only as a compile error inside the generated project
  while `generate` reported "0 characterized":
  - `init-tests --tfm net48` scaffolded `Nullable`/`ImplicitUsings`, which .NET Framework rejects
    because it defaults to C# 7.3 (CS8630). Framework targets now pin `<LangVersion>latest</LangVersion>`.
  - The generated setup file used `[ModuleInitializer]`, absent from the .NET Framework BCL and
    `internal` in the packages that ship a copy (CS0122). It now declares its own under
    `#if !NET5_0_OR_GREATER`.

  Verified end to end on the bundled v4.8 sample: 2/2 characterized, real bracket boundaries locked
  (99999/100000/100001), and `verify` reports behavior preserved.

### Added
- `integrations/github-copilot-upgrade/` — a custom upgrade instruction for Microsoft's Copilot upgrade
  agent, so it locks behavior with Pinion before transforming code and verifies afterwards. Every
  command in it was checked against the real CLI, and the loop was run end to end on the LegacyShop
  sample, including confirming that `verify` catches a one-digit change to a locked method.

## [1.1.0] - 2026-08-24

### Added
- **OpenAI-compatible AI provider**, opt-in like every other outbound path: `--provider openai` and
  `--provider azure-openai`. The same client also drives any OpenAI-compatible server via `--base-url`
  (Ollama, vLLM, LM Studio, LiteLLM), which makes AI-assisted generation possible without a
  third-party API at all. Azure uses its own `api-key` header and deployment route; `--model` there is
  your deployment name. Keys come from `OPENAI_API_KEY` / `AZURE_OPENAI_API_KEY`.
- `--model` and `--base-url` now default per provider, so `--provider openai` no longer inherits an
  Anthropic model id.

### Changed
- `ILlmClient` gained `PreviewRequest`, so `--dry-run` renders each provider's OWN wire format.
  Previously the preview was hardcoded to the Anthropic shape; with a second provider that would have
  printed a payload the tool would never send, defeating the point of the flag.
- Removed paid-tier language from user-visible surfaces: `generate` help text, the `--license`
  description, and — importantly — the CI workflow files `pinion ci` writes into user repos, which
  told them the mutation-score step needed a `PINION_LICENSE` secret. Nothing is gated.

### Fixed
- The secret scrubber missed modern OpenAI keys. `sk-[A-Za-z0-9]{20,}` stopped at the hyphen in
  `sk-proj-…` / `sk-svcacct-…`, leaving most of the key in an outbound payload.
- TRUST.md's audit command was inaccurate: grepping for `HttpClient` also matched `SeamAnalyzer` and
  `SeamRewriter`, where the name appears in a list of *strings* Pinion detects in your code. It now
  greps for construction and sending, which returns exactly the two provider clients.

## [1.0.0] - 2026-08-24

First public release. The full local-first workflow: audit a legacy .NET codebase, lock what it
does today, migrate, and prove nothing changed.

### Added
- `analyze` — free, offline migration-readiness audit: risk ranking, complexity, blast radius, domain
  tags, Feathers seam analysis, migration landmines, and a self-contained HTML dashboard.
- `quickstart` — one command to lock your riskiest behaviors: analyze → scaffold a test project →
  characterize, fully offline.
- `seam` — turn the seam report into treatment: automatically introduce Feathers seams for ambient
  values (`DateTime.Now`, `Guid.NewGuid`, …). The original signature becomes a delegating wrapper
  (existing callers unchanged); the real body moves to an overload whose ambient values are parameters,
  which `generate` can then lock deterministically. Preview-by-default; applied edits are compile-gated
  and reverted if the build breaks. Resource obstacles (File, HttpClient, …) stay flagged as manual.
- `generate` — deterministic characterization-test synthesis (no AI, reproducible), with an opt-in
  Anthropic provider. Handles constructors, async, ref/out, throwing getters, interface stubs, and
  framework value maps; non-deterministic targets are detected and quarantined with a seam diagnosis.
- `verify` — re-run the locked suite against current code and report identical-vs-changed; `--since <ref>`
  scopes a run to what a change (plus its callers) touched. CI gate via exit code.
- `accept` — re-baseline intended behavior changes.
- `prove` — mutation-test the generated suite (Stryker.NET) for a regression-catching score.
- `ci` — scaffold a GitHub Actions / Azure DevOps behavior gate.
- VB.NET support across `analyze` **and** `generate`: the analyze adapter reaches parity with C#
  (complexity, blast radius, tags, landmines, tests), and deterministic synthesis resolves and mines
  branch constants out of VB syntax (`Select Case`, `Case Is`/ranges, comparisons) to emit a **C#**
  characterization test against the VB assembly — one golden-master pipeline for both .NET languages.
  `init-tests` accepts a `.vbproj`; the AI providers stay C#-only.
- **License: source-available under the Functional Source License 1.1 (`FSL-1.1-ALv2`).** Free for all
  use — commercial, internal, on proprietary code, and on client engagements. The only right withheld
  is Competing Use (reselling, rehosting, or repackaging Pinion itself). Each released version
  automatically converts to Apache-2.0 two years after release.
- The offline signed (ECDSA P-256) license gate remains in the tree but is **dormant — it enforces
  nothing**; `generate` and `prove` run for everyone without a key. Retained only to keep a
  separately-negotiated commercial agreement possible later. The prior commercial EULA draft is
  archived, superseded, at `docs/EULA-draft-superseded.md`.
- Packaging: ships as a .NET tool (`dotnet tool install -g Pinion`); tag-driven NuGet release workflow.
- Generated tests pin invariant culture and carry a format stamp (format **v1**) for cross-machine
  reproducibility and forward-compatible verification.

### Notes
- Trust: `analyze`, deterministic `generate`, `verify`, and `prove` run with no network access.
- Pinion is *source-available*, not OSI-approved open source: the Competing Use carve-out restricts a
  field of endeavor, which the Open Source Definition forbids. The docs use the accurate term.
- Before the first publish: have `LICENSE.md` reviewed by a lawyer, set the copyright holder to a legal
  entity if one is formed, and set the NuGet API key.
