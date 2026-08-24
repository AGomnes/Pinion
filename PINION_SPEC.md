# BehaviorLock — Project Specification & Build Brief

> A tool that generates **characterization tests** for legacy .NET codebases so teams
> (and AI migration tools) can change everything **without breaking the base** — and prove it.
>
> This document is the source of truth for Claude Code. Read it fully before writing code.
> Working name: **BehaviorLock** (rename freely).

---

## 0. TL;DR for the agent

Build a **.NET 8 CLI global tool** that:
1. **`analyze`** — uses Roslyn to scan a legacy C# codebase, find risky *unprotected* (untested) code, and emit a ranked **Migration Readiness Report**. (Free tier. No AI needed.)
2. **`generate`** — for chosen targets, uses an LLM to write **characterization tests**, runs them against the *current* code, and records actual output as a **golden master** (approval testing). (Originally specced as a paid tier; Pinion is now source-available — see LICENSE.md.)

The core principle: the tool **never decides what code *should* do**. It measures where the team
is exposed (analyze) and **freezes what the code *currently* does** (generate). That is what makes
it trustworthy and buildable by one person — it reads and records, it does not infer business intent.

Build **`analyze` first, end to end**, before touching the LLM layer.

---

## 1. Why this exists (context for good decisions)

Legacy .NET codebases facing migration (e.g. .NET Framework 4.8 → .NET 8/10, deadline pressure
around .NET 8 end-of-support Nov 2026) are full of code with **no tests**. Teams — and AI refactoring
tools — change this code and silently alter behavior. The established fix is **characterization testing**
(Michael Feathers, *Working Effectively with Legacy Code*): capture what the code *actually does today*
(bugs included) as a safety net, then refactor/migrate freely; any behavior change fails a test.

The market gap: every 2026 AI-testing tool targets **forward UI/E2E test generation**. None generate
**characterization tests that lock existing backend behavior before a migration**. That slot is open.

**Product promise:** *Change everything, break nothing, and prove it.*

---

## 2. Architecture overview

Two halves, separated by a clean boundary so other languages can be added later.

```
┌─────────────────────────────────────────────────────────────┐
│ LANGUAGE-AGNOSTIC ENGINE  (~70% of value, write once)        │
│  - risk scoring        - report generation                   │
│  - target selection    - AI prompt orchestration             │
│  - run + snapshot loop  - secret scrubbing                    │
│  - CI integration      - caching / batch                     │
│         talks ONLY to the IR below, never to Roslyn types     │
└───────────────────────────┬─────────────────────────────────┘
                            │  ILanguageAdapter  (the boundary)
┌───────────────────────────┴─────────────────────────────────┐
│ LANGUAGE ADAPTER (per language)                              │
│  C# / .NET  → Roslyn (Analyze) + xUnit/Verify (Generate/Run) │
│  later: TS  → ts-morph + Jest                                │
│  later: Py  → ast + pytest                                   │
└─────────────────────────────────────────────────────────────┘
```

### 2.1 The critical design rule
Define a **language-neutral intermediate representation (IR)** on day one. The engine operates only
on the IR. Roslyn types must **never leak** past the adapter. Get this right and a new language is a
new adapter (a weekend), not a rewrite.

```csharp
// The IR the whole engine speaks. Adapters produce this; engine consumes it.
public sealed record CodeUnit(
    string Id,                 // stable identifier, e.g. "Ns.Class.Method(int,string)"
    string DisplayName,
    string FilePath,
    int    StartLine,
    int    EndLine,
    string Signature,
    IReadOnlyList<ParamInfo> Parameters,
    string ReturnType,
    int    CyclomaticComplexity,
    int    LineCount,
    IReadOnlyList<string> CallerIds,   // who calls this (blast radius)
    IReadOnlyList<string> CalleeIds,   // what this calls
    IReadOnlyList<string> DomainTags,  // "money","auth","date","io","data-transform"
    bool   HasTests,                   // referenced by any test project?
    bool   IsPublicEntryPoint,
    IReadOnlyList<string> MigrationLandmines // "WebForms","WCF","EF6"
);

public sealed record ParamInfo(string Name, string Type);

public interface ILanguageAdapter
{
    string Language { get; }                       // "csharp"
    Task<IReadOnlyList<CodeUnit>> AnalyzeAsync(string projectOrSolutionPath, CancellationToken ct);
    Task<GeneratedTest> EmitTestAsync(CodeUnit unit, string generatedBody, CancellationToken ct);
    Task<ExecutionResult> RunAndSnapshotAsync(GeneratedTest test, CancellationToken ct);
}
```

---

## 3. Tech stack (all free, all local-first)

| Concern | Choice | Notes |
|---|---|---|
| Runtime | .NET 8 global tool | `dotnet tool install -g` distribution |
| Code analysis (C#) | **Roslyn** (`Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`) | the official compiler-as-API; authoritative, runs locally |
| Approval/golden-master | **Verify** (Simon Cropp) | don't hand-roll snapshot capture/diff/review |
| Coverage | **Coverlet** | prove generated tests actually exercise the code paths |
| Mutation testing (premium, later) | **Stryker.NET** | proves tests would catch a regression |
| Property-based (selective) | **CsCheck** / **FsCheck** | for pure functions (VAT/pricing); surgical use only |
| AI | **Anthropic API** via thin HTTP client | ZDR option; also support local-model endpoint |
| CLI framework | `System.CommandLine` | |

**Deliberately NOT used:** ML risk models (explainability > marginal accuracy), LangChain-style
orchestration (want full control of the security boundary), vector DB / RAG (we have exact source via
Roslyn), microservices (a single CLI is the correct shape).

---

## 4. What `analyze` looks for

Goal: find code that is **risky to change AND currently unprotected**.

1. **Public entry points** — methods, API controllers/endpoints, public service classes. Their behavior
   is a contract. Private helpers are exercised transitively; deprioritize.
2. **Absence of tests** — does any test project reference this symbol? If no → unprotected.
3. **Risk signals** (feed the score):
   - high **cyclomatic complexity** (many branches = many behaviors to lock)
   - **domain sensitivity** — touches money/tax/auth/pricing/dates/data-transforms (detect by name + by callees)
   - **size** (the 500-line methods nobody dares touch)
   - **blast radius** — number of callers (from the call graph)
4. **Migration landmines** — flag .NET Framework→Core killers separately: **WebForms, WCF contracts,
   EF6 patterns**. These cause most migration failures and are behavior-change risks.

### 4.1 Risk score — keep it transparent
A weighted, explainable formula. **No ML.** Clients must understand *why* something ranked high.

```
risk = w1*norm(complexity)
     + w2*(hasTests ? 0 : 1)
     + w3*domainSensitivity      // 0..1 from tags
     + w4*norm(callerCount)
     + w5*norm(lineCount)
     + w6*(hasMigrationLandmine ? 1 : 0)
```
Start with equal-ish weights, expose them in config, tune against real repos. Always show the score
breakdown in output so it's auditable.

---

## 5. Algorithms / methods to implement

- **Cyclomatic complexity** — count decision points (`if`/`else`/`case`/`&&`/`||`/`?:`/loops/`catch`)
  by walking the Roslyn syntax tree per method.
- **Call-graph construction** — directed graph who-calls-what from the semantic model → blast radius +
  **seam** identification (Feathers' safe wrap points).
- **Coupling (fan-in/fan-out)** — cheap from the call graph; sharpens risk.
- **Coverage measurement** (Coverlet) — turns "we wrote tests" into "we cover 87% of branches in the
  billing module" — a verifiable claim.
- **Mutation testing** (Stryker.NET, later/premium) — proves protectiveness, not mere presence.
- **Self-verification repair loop** (AI layer, see §6) — the single most important reliability technique.

---

## 6. What `generate` does (the AI layer)

Pipeline per target CodeUnit:

```
extract context (Roslyn: source + param/return types + callee signatures)
   → LLM: "generate xUnit characterization tests; diverse inputs incl. edge cases;
           DO NOT assert expected values — leave assertions to approval capture"
   → COMPILE the generated test
   → if compile/error: feed error back to model, repair  (loop, max N tries)
   → RUN against the REAL current code
   → capture actual output as golden master (Verify .approved.txt)
   → write test files + snapshots into the repo for human review
```

### 6.1 Key inversion (do not get this wrong)
We **do not** assert what the code *should* return. We **record what it *does*** return — bugs become
locked-in "current behavior," which is correct for a safety net. The assertion is the snapshot diff.

### 6.2 Self-verification loop
Never trust generated tests blind. generate → compile → run → on failure feed the error back to the
model to fix. This compile-execute-repair loop separates a reliable tool from a demo. (Same agentic
pattern as Claude Code, applied internally.)

### 6.3 Cost controls (the API is cheap at this scale)
~$0.04/method on Sonnet before discounts → ~$40 for a 1000-method codebase → single-digit dollars with
**prompt caching** (90% off the stable project-context prefix) + **Batch API** (50% off; generation is
not latency-sensitive). Route cheap structural passes to Haiku, reserve Sonnet for test-body reasoning.
Output tokens are 5× input — keep generated tests focused.

---

## 7. Information security (this IS the sales pitch)

The product asks enterprises to let a tool read their source. Trust is the whole game.

- **Code stays local.** CLI runs inside their environment. Source never leaves their network. This is
  why CLI-first is the trust architecture, not just an MVP convenience.
- **Only outbound data = per-method context to the AI.** Send the **minimum** (method + type signatures),
  never connection strings/secrets/config.
- **Pre-send scrubber** — strip obvious secrets (keys, passwords, tokens) before anything leaves; allow
  marking files/namespaces as never-send.
- **Zero Data Retention** — use Anthropic ZDR; verify current terms and link clients to official docs
  (don't paraphrase the guarantee).
- **Local-model mode** — let the tool target a local LLM endpoint for air-gapped/regulated clients
  (finance/gov/health). Same price; everything stays in-house.
- **Publish the source** — anyone can verify it phones home nowhere except the one declared call.
- **Auditable network behavior** — single outbound endpoint, `--dry-run` showing exactly what bytes
  would be sent, verbose logging of every external call, **no telemetry by default** (opt-in only).
- **Local-only artifacts** — generated tests + snapshots written to their repo; we never receive/store them.
- **Legal** — DPA (processor) + NDA for service engagements; be ready to speak GDPR (source usually isn't
  personal data, but captured test snapshots might contain real data → scrubbing matters).

One-line client pitch: *"Runs entirely on your machine, reads your code locally. The only thing that
ever leaves is individual method snippets sent to the AI under zero-retention terms — and if that's too
much, run it fully offline against a local model. The source is public; verify every word."*

---

## 8. Output formats

### 8.1 `analyze` → Migration Readiness Report (the sales asset)
Human-readable (console + Markdown) **and** machine-readable (JSON). Example shape:

```
MIGRATION READINESS REPORT — MyLegacyERP.csproj
Scanned: 1,247 methods across 83 files

Behavior coverage:        18% (224 of 1,247 methods tested)
High-risk & UNPROTECTED:  61 methods
Migration landmines:      3 WCF contracts, 1 WebForms page, 12 EF6 queries

TOP RISK HOTSPOTS
─────────────────────────────────────────────
1. InvoiceService.CalculateVat()   risk 9.4  ← money, complex(14), 0 tests, 14 callers
2. AuthHandler.ValidateToken()     risk 9.1  ← auth, 0 tests, 22 callers
3. PriceEngine.ApplyDiscounts()    risk 8.7  ← money, 180 lines, 0 tests
...
─────────────────────────────────────────────
Estimated behavior-lock effort: 61 methods → ~2 days
```
Each row must expose its score breakdown (auditable). JSON output drives the `generate` step and
any future web UI.

### 8.2 `generate` → committed artifacts
Test classes + `.approved.txt` snapshots written into the target repo. From then on, CI fails the moment
a migration changes behavior. We store/receive nothing.

---

## 9. Build order (do NOT skip ahead)

**Milestone 1 — `analyze` skeleton (no AI):**
- Load project/solution via `MSBuildWorkspace`.
- Build IR: enumerate methods, signatures, line counts.
- Compute cyclomatic complexity.
- Detect `HasTests` (test-project references to symbols).
- Console + Markdown + JSON report.
- ✅ This alone is the **free Readiness Audit** and the lead magnet.

**Milestone 2 — enrich `analyze`:**
- Call-graph → caller/callee counts (blast radius).
- Domain tagging (name + callee heuristics).
- Migration-landmine detection (WebForms/WCF/EF6).
- Weighted risk score with visible breakdown.
- Coverlet integration for real coverage %.

**Milestone 3 — `generate` core (AI):**
- Context extraction per target.
- Thin Anthropic client (+ ZDR, + local-model endpoint option).
- Pre-send secret scrubber + `--dry-run`.
- Test emission (xUnit + Verify).
- **Compile → run → repair loop.**
- Snapshot capture → write artifacts.

**Milestone 4 — hardening / premium:**
- Prompt caching + Batch API.
- Stryker.NET mutation-testing tier.
- Selective property-based generation (CsCheck) for pure functions.
- CI templates (GitHub Actions / Azure DevOps).

**Always:** keep the `ILanguageAdapter`/IR boundary clean so TS/Python/Java adapters can follow later.
Resist multi-language until .NET is paying — the architecture keeps the door open cheaply; the business
says don't walk through it yet.

---

## 10. Reference reading
- Michael Feathers, *Working Effectively with Legacy Code* — the product is a productization of this
  book's central technique. Vocabulary (seams, characterization tests, the legacy-code dilemma) is what
  the senior engineers who approve the purchase already respect.

---

## 11. Coding conventions for the agent
- Single CLI project + a clean engine library + an adapter library. No microservices.
- Engine library must not reference `Microsoft.CodeAnalysis` — only the adapter does.
- Everything testable; the tool that locks behavior must have its own tests.
- Security-sensitive paths (anything outbound) must be small, isolated, logged, and dry-run-able.
- Prefer explicit, explainable logic over cleverness — especially in risk scoring.
- Start with Milestone 1. Get it running on a real repo before moving on.
