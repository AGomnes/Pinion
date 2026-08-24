# Changelog

All notable changes to Pinion are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project follows
[Semantic Versioning](https://semver.org/) from its first published release.

The **generated test/snapshot format** is versioned separately and stamped into every generated test
(`// pinion-format: N`); a bump there is called out under the release that changes it.

## [Unreleased]

First release candidate — the full local-first workflow, pending public publish.

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
