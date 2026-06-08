# Data Processing Agreement — Template

> ⚠️ **This is a template, not legal advice.** It is a starting point to hand to counsel, not a
> ready-to-sign contract. Laws (GDPR, UK GDPR, CCPA/CPRA, sector rules) and your specific engagement
> determine what is actually required. Have a qualified lawyer review and adapt it before use. Bracketed
> `[…]` fields are placeholders.

This DPA supplements the agreement between **[Customer]** ("Controller") and **[Provider]**
("Processor") for the provision of Pinion-related services ("Services").

---

## 1. Subject matter and roles
The Processor processes Personal Data on behalf of the Controller solely to provide the Services. The
Controller is the controller; the Processor is the processor.

## 2. Nature and purpose of processing
Generating and verifying characterization tests for the Controller's .NET source code. Pinion runs in
the Controller's environment; source code is read locally. The only data that leaves the Controller's
environment is per-method code context sent to the configured AI provider when the Controller opts into
the AI tier — and only if the Controller chooses a non-local provider. See `TRUST.md`.

## 3. Duration
For the term of the underlying agreement, and any wind-down period needed to return or delete data.

## 4. Types of data and categories of data subjects
- **Expected:** source code and type signatures, which are generally **not** Personal Data.
- **Possible:** captured test snapshots ("golden masters") may incidentally contain real values that
  constitute Personal Data. The Processor's secret scrubber redacts secret-shaped values before any data
  leaves the machine and before snapshots are written, but the Controller remains responsible for data
  classification of its own code and test data.
- **Data subjects:** any individuals whose data may incidentally appear in the above.

## 5. Sub-processors
- **[AI provider, e.g. Anthropic]** — only when the Controller enables the AI generation tier with a
  non-local provider; receives per-method code context. The Controller may enable Zero Data Retention
  with the provider, and may instead run a fully local model or the deterministic (no-AI) generator so
  that **no** Sub-processor receives any data.
- List any others: `[…]`.
The Processor will inform the Controller of changes to Sub-processors and allow reasonable objection.

## 6. Controller instructions
The Processor processes Personal Data only on the Controller's documented instructions, including the
choice of generation provider and any `--no-send` / `.pinionnosend` exclusions the Controller configures.

## 7. Confidentiality
Personnel authorized to process Personal Data are bound by confidentiality obligations.

## 8. Security measures
- Local-first execution; source code is not transmitted except as described in §2.
- A single declared outbound endpoint; no telemetry.
- Pre-send secret scrubbing of outbound context and written snapshots.
- Never-send allowlist for designated files/namespaces.
- Optional fully-local / air-gapped operation (deterministic generator or local model endpoint).
- `[Controller/Processor to add: access controls, encryption in transit/at rest, logging, etc.]`

## 9. Data subject rights
The Processor will assist the Controller, as reasonably practicable, in responding to data subject
requests, taking into account the nature of the processing.

## 10. Personal data breach
The Processor will notify the Controller without undue delay after becoming aware of a Personal Data
breach affecting the Controller's data, with available details to support the Controller's obligations.

## 11. International transfers
Any transfer outside `[jurisdiction]` will rely on an approved transfer mechanism `[e.g. SCCs]`. Note:
in fully-local mode no transfer occurs.

## 12. Return and deletion
On termination, at the Controller's choice, the Processor returns or deletes Personal Data, except where
retention is legally required. Generated tests and snapshots reside in the Controller's repository; the
Processor receives and stores none.

## 13. Audits
The Processor makes available information necessary to demonstrate compliance and allows for reasonable
audits. The open-source engine and `--dry-run` payload preview support independent verification.

---

_Signatures, effective date, and governing law: `[…]`._
