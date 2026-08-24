# Pinion

**Migrate with AI. Prove it didn't break anything.**

Pinion locks what your legacy .NET code *does today* into runnable tests — then tells you exactly
what a migration changed. It runs entirely on your machine.

AI migration tools will happily rewrite your .NET Framework app. They validate the result by
checking that your build and tests pass — which proves almost nothing on the codebases that most
need migrating, because those are the ones without tests.

Pinion closes that gap in three commands:

```pwsh
dotnet tool install -g Pinion

pinion analyze  ./MyApp                  # where am I exposed? (free, offline, no AI)
pinion generate ./MyApp -p ./MyApp.Tests # lock what the code does TODAY as golden masters
#          … now migrate: Copilot, a contractor, or by hand …
pinion verify   ./MyApp                  # identical — or exactly what changed, and where
```

Pinion never decides what your code *should* do. It records what it *actually does*, bugs included,
and freezes that. It's a measurement, not an opinion — which is what makes it safe to point at code
nobody on the team understands any more. The idea is Michael Feathers' **characterization testing**
(*pinning tests*), automated for .NET — **C# and VB.NET**.

## Nothing leaves your machine

The `analyze` path and the default deterministic `generate` path make **zero network calls** — there
is no HTTP client in them at all. No telemetry, no analytics, no licence activation, no update check.
AI is strictly opt-in (`--provider anthropic`), off by default, and never required.

That matters if you're in regulated finance, defence, healthcare, or anywhere air-gapped — the
places still running the oldest .NET, and the ones cloud-only migration tooling can't serve.

## Commands

| | |
|---|---|
| `analyze` | migration-readiness audit: risk ranking, blast radius, seams, landmines, HTML dashboard |
| `generate` | synthesize characterization tests and capture golden masters |
| `verify` | re-run the locked suite after a migration; report identical vs. changed |
| `accept` | re-baseline intended behavior changes |
| `prove` | mutation-test the generated suite (Stryker.NET) |
| `seam` | introduce Feathers seams for ambient values so untestable methods become lockable |
| `ci` | scaffold a GitHub Actions / Azure DevOps behavior gate |
| `quickstart` | analyze → scaffold → lock your riskiest behaviors, in one command |

## Links

- **[Full documentation and source](https://github.com/AGomnes/Pinion)**
- **[PROOF.md](https://github.com/AGomnes/Pinion/blob/main/PROOF.md)** — a real run on nopCommerce
  (4,072 methods): 2,189 high-risk untested methods found, 17 service methods locked offline for $0
- **[TRUST.md](https://github.com/AGomnes/Pinion/blob/main/TRUST.md)** — what leaves the machine, per command
- **[SECURITY.md](https://github.com/AGomnes/Pinion/blob/main/SECURITY.md)** — reporting, egress, and the fact that `generate` executes your code
- **[CHANGELOG.md](https://github.com/AGomnes/Pinion/blob/main/CHANGELOG.md)**

## Licence

Source-available under the [Functional Source License 1.1, Apache-2.0 future grant](https://github.com/AGomnes/Pinion/blob/main/LICENSE.md)
(`FSL-1.1-ALv2`). Free for **all** use — commercial, internal, on proprietary code, and on client
engagements. The only right withheld is Competing Use: reselling, rehosting, or repackaging Pinion
itself. Each released version converts to Apache-2.0 two years after release.
