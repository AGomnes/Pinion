# Security Policy

Pinion is a tool you point at proprietary source code and, on `generate`, one that **executes**
that code to capture its behavior. That deserves a plain statement of how it handles your data and
how to report a problem.

## Reporting a vulnerability

Please report privately — **do not open a public issue.**

Use [GitHub private vulnerability reporting](https://github.com/AGomnes/Pinion/security/advisories/new)
(Security → Report a vulnerability). If that is unavailable to you, email the maintainer via the
address on the commits in this repository.

Please include the version (`pinion --version`), your OS and .NET SDK, the command you ran, and a
minimal reproduction if you have one. **Do not include proprietary source** in a report — a
redacted snippet or synthetic reproduction is always enough.

Expect an acknowledgement within a few days. This is a small project without a paid security team,
so please allow reasonable time for a fix before public disclosure. Credit is given in the
changelog unless you prefer otherwise.

## What leaves your machine

The short version: **by default, nothing.**

| Command | Network |
|---|---|
| `analyze`, `verify`, `accept`, `seam`, `ci`, `init-tests` | none |
| `generate` (default, `--provider deterministic`) | none |
| `prove` | none by Pinion; Stryker.NET may restore NuGet packages |
| `generate --provider anthropic` (opt-in, never the default) | per-method context to the Anthropic API only |

There is no telemetry, no analytics, no licence activation call, and no update check. The
deterministic path has no HTTP client in it at all. [TRUST.md](TRUST.md) documents this per command
against the actual code, and the source is public so you can verify rather than trust.

If you enable the AI provider, these apply:

- A **secret scrubber** strips keys, passwords, tokens, and connection-string credentials before
  anything is sent.
- `--no-send` (and a `.pinionnosend` file) marks files or namespaces whose source must **never**
  leave the machine. It is enforced at the outbound boundary — such code is never read into a
  payload at all, including under `--dry-run`.
- `--dry-run` prints the exact bytes that would be sent and makes no network call.
- `--base-url` points the client at a local or self-hosted model instead.

## Executing your code

`generate` runs the target code to record what it does. That is inherent to characterization
testing, and it is the sharpest edge in this tool. Mitigations:

- Methods tagged `io` (may touch filesystem/DB/network) are **skipped unless** you pass
  `--allow-side-effects`. Methods tagged `money`/auth still run, but are called out up front so you
  review their snapshots before committing.
- `--exclude` and `.pinionignore` remove methods from consideration entirely.
- A per-method timeout (`--timeout`, default 180s) kills hung or infinite-looping targets.
- Captured golden masters are scrubbed before being written, because return values can contain
  real secrets or PII — review snapshots before committing them.

**Run `generate` against a development checkout with test credentials, never production
configuration.** Pinion cannot tell that a method named `SendInvoice` really sends invoices.

## Secrets in this repository

The licensing signing key is a private key and has never been committed; only the corresponding
**public** key ships in the binary, which can verify but not mint. `.gitignore` excludes
`tools/pinion-signing-key.txt` and `*.license`.

## Supported versions

Pre-1.0: fixes land on `main` and in the next release. Please report against the latest version.
