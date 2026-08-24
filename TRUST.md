# Trust & Data Handling

Pinion asks to read your source code, so this document states exactly what does and does not leave
your machine. Every claim here is checkable against the published source; where to look is noted
inline.

**One line:** Pinion runs entirely on your machine. The *only* thing that can ever leave is individual
method snippets, and only when you explicitly opt into an AI provider (`generate --provider anthropic|openai|azure-openai`).
Everything else — analysis, the default test generator, verification, mutation testing — is 100% local
with no network access. If even per-method snippets are too much, run fully offline against a local
model or the deterministic generator.

---

## What leaves the machine, per command

| Command | Network? | What leaves |
|---|---|---|
| `analyze` | **No** | Nothing. Roslyn reads your code locally; the report is written to disk. |
| `generate` *(default, `--provider deterministic`)* | **No** | Nothing. Tests are synthesized locally from the semantic model. $0, reproducible. |
| `generate --provider anthropic` | **Yes — one endpoint** | Per-method context only (see below), to Anthropic. Opt-in. |
| `generate --provider openai` | **Yes — one endpoint** | Per-method context only, to OpenAI (or whatever `--base-url` names). Opt-in. |
| `generate --provider azure-openai` | **Yes — one endpoint** | Per-method context only, to your own Azure resource. Opt-in. |
| `generate --provider heuristic` | **No** | Nothing. An offline stand-in used to exercise the pipeline. |
| `verify` | **No** | Nothing. Runs the locked tests locally and diffs snapshots. |
| `prove` | **No** | Nothing. Runs Stryker.NET mutation testing locally. |
| `ci` | **No** | Nothing. Writes a workflow file. |

There is **no telemetry, no analytics, no "phone home"** in any command — not opt-out, simply not present.

---

## The outbound endpoints, and every one is opt-in

Pinion makes **no network request at all** unless you explicitly name an AI provider: `--provider
anthropic`, `openai`, or `azure-openai`. None of them is the default and nothing selects one for you —
the default `generate` provider is `deterministic`, which has no network path. With no such flag, the
code below is never reached.

A key sitting in the environment changes nothing on its own. `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` and
`AZURE_OPENAI_API_KEY` are each read only inside the branch their own `--provider` selects
([`GenerateCommand`](src/Pinion.Cli/GenerateCommand.cs)), so on a default run Pinion never reads any of
them. The flag is the only thing that enables an outbound path, and CI runners or shared machines that
happen to have a key in the environment are unaffected.

Exactly two files in Pinion can make a network request, one per AI provider, and you can list them
yourself:

```sh
grep -rln "new HttpClient\|\.SendAsync(" src/ --include=*.cs
#   → src/Pinion.Generate/AnthropicClient.cs   (POST /v1/messages)
#   → src/Pinion.Generate/OpenAiClient.cs      (POST /chat/completions)
```

(Search for the bare word `HttpClient` instead and you also hit `SeamAnalyzer`/`SeamRewriter`, where it
appears in a *list of strings* — the resource names Pinion detects in **your** code when reporting seam
obstacles. Those are data, not calls, which is why the command above matches construction and sending.)

Each sends to one endpoint and runs only when its own `--provider` is passed:

| `--provider` | Client | Key |
|---|---|---|
| `anthropic` | `AnthropicClient` | `ANTHROPIC_API_KEY` |
| `openai` | `OpenAiClient` | `OPENAI_API_KEY` |
| `azure-openai` | `OpenAiClient` (Azure auth + deployment route) | `AZURE_OPENAI_API_KEY` |

Every one of them is off unless you name it. The base URL is overridable (`--base-url`) on all of them,
so the same clients target a **local, air-gapped endpoint** — an Anthropic-compatible server, or any
OpenAI-compatible one such as Ollama, vLLM or LM Studio — with no change and no key leaving your
network.

### What is sent
The minimum needed to write a characterization test for one method: **the method's source and the type
signatures it needs** — assembled by the adapter's context extractor. See
[`GenerationContext`](src/Pinion.Generate/GenerationModels.cs) (*"This is the ONLY code that leaves the
machine — keep it to the method plus the type signatures it needs, never config/secrets."*).

### What is NOT sent
Connection strings, passwords, API keys, tokens, configuration, and whole files are not sent. Two
defenses enforce this, both before any byte leaves:

1. **Pre-send secret scrubber** — [`SecretScrubber`](src/Pinion.Generate/SecretScrubber.cs) redacts
   secret-shaped values (password/token/key assignments, connection-string credentials, known token
   shapes like `sk-ant-…` and `sk-proj-…`, AWS keys, JWTs, PEM blocks) from every outbound payload **and** from the
   golden masters it writes to disk.
2. **Never-send allowlist** — mark files or namespaces whose source must never be sent
   (`--no-send <glob>`, or a `.pinionnosend` file). Enforced at the outbound boundary in
   [`TestGenerator`](src/Pinion.Generate/TestGenerator.cs) *before* any context is extracted, so a
   never-send unit's source is never read into a payload, never previewed, and never sent. Such methods
   are still locally characterizable with `--provider deterministic`.

### Your own API key is protected from the code Pinion runs
`generate` *executes* the code under characterization (via `dotnet test`) to capture its behavior. Child
processes normally inherit the parent's environment — so without care, a method under test could read
a provider key out of its own process environment and exfiltrate it. Pinion **strips every provider
API-key variable (Anthropic, OpenAI, Azure OpenAI and others) from the environment of every child
process it launches**
([`ProcessRunner.ScrubSecretsFrom`](src/Pinion.Adapters.CSharp/ProcessRunner.cs)), so the code it runs
never sees the key. A key is read only from the environment, travels only as an auth header
(`x-api-key` for Anthropic, `Authorization: Bearer` for OpenAI, `api-key` for Azure), and is never
written to disk, logged, or placed in a request body or `--dry-run` output.

### Audit exactly what would be sent — `--dry-run`
`generate … --dry-run` prints the *exact bytes* that would be POSTed and makes **no** network call.
This is the literal payload — run it and read it.

```pwsh
pinion generate <path> --target <method> --provider anthropic --dry-run
```

---

## Zero Data Retention (ZDR)

Anthropic offers Zero Data Retention as an **organization-level setting on your account** — it is not a
request header Pinion can assert on the wire. Enable it in the Anthropic console and verify the current
terms directly with Anthropic; do not rely on this document paraphrasing their guarantee. With ZDR on,
the per-method snippets sent for generation are not retained.

---

## Air-gapped / local-only mode

For regulated or air-gapped environments, Pinion can run with **nothing leaving the machine at all**:

- **Default deterministic generator** — `generate` (no `--provider`) synthesizes tests locally from the
  semantic model. No network, no key, $0, and reproducible.
- **Local model endpoint** — for AI-assisted generation without a third-party API, point a client at a
  server on your own network: `--provider anthropic --base-url http://localhost:<port>` for an
  Anthropic-compatible server, or `--provider openai --base-url http://localhost:11434` for any
  OpenAI-compatible one (Ollama, vLLM, LM Studio, LiteLLM). Same code path, your network only.

`analyze`, `verify`, and `prove` are already fully local, so a complete readiness audit → lock → migrate
→ verify loop can run with no outbound traffic.

---

## Local-only artifacts

Generated tests and golden masters are written only under `<test-project>/PinionCharacterization/`
(name-sanitized so a path can't escape it). Reports (`--out`) are written where you ask. The HTML
report is a **static, self-contained file** — inlined CSS, no external scripts, no CDN, no JavaScript
that calls out. Pinion never starts a web server or binds a port. Nothing is sent back to us; we
receive and store none of your tests, snapshots, or reports.

---

## Verify it yourself

All of Pinion's source is public, under [FSL-1.1-ALv2](LICENSE.md). It is source-available rather than
OSI-approved open source, but every line is readable, so you do not have to trust this document: read
the code, run `--dry-run`, and grep for the single outbound call. Quick audit:

```sh
grep -rn "HttpClient\|SendAsync"            src/   # the one outbound client
grep -rn "telemetry\|analytics\|track"      src/   # (none)
pinion generate <path> -t <m> --provider anthropic --dry-run   # the exact outbound bytes
```

---

## Legal

For service engagements, a Data Processing Agreement (processor terms) and NDA are appropriate. A
starting-point DPA template is in [docs/DPA-template.md](docs/DPA-template.md) — **it is a template, not
legal advice; have counsel review and adapt it.** Source code is usually not personal data, but captured
test snapshots can contain real values, which is why the scrubber runs on golden masters too; discuss
data classification with your DPO where relevant.
