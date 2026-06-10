# Billing & Licensing Server — Requirements & Setup

The server-side backend that turns a Pinion subscription into a working license. Self-contained
reference for provisioning and operating it on a self-hosted Supabase server.

**One line:** payment (Vipps for Norway, Paddle/Polar for international) → webhook → a Supabase function
mints a short-lived **ECDSA-signed** license → stored, emailed, and refreshable by the CLI. Everything
except the payment processor's fee runs on infrastructure you already have.

---

## 1. Architecture

```
                  ┌─ "Pay with Vipps"  → Vipps Recurring (agreement + charges)  ┐
 /products/pinion ┤                                                             ├→ webhook → Supabase fn
                  └─ "Pay with card"   → Paddle/Polar hosted checkout (MoR)     ┘        │
                                                                                          ▼
                          upsert subscription  →  mint-license (ECDSA P-256)  →  store  →  email + dashboard
                                                                                          │
   pinion CLI  ── `license refresh` (license-only call, no source code) ─────────────────┘
```

Two payment rails feed **one** entitlement table. The license layer is provider-agnostic — it only
records which rail paid. Why hybrid: native **Vipps** gives the best Norwegian conversion (and Norwegian
**MVA** is trivial for a Norwegian business), while a **Merchant-of-Record** (Paddle/Polar) absorbs all
*international* VAT — the part that is genuinely hard. Pure MoR providers do **not** support Vipps, which
is why both rails exist.

---

## 2. The license format (what we mint)

Defined by the product in [`src/Pinion.Generate/Licensing/License.cs`](../src/Pinion.Generate/Licensing/License.cs);
minted today by [`tools/Pinion.LicenseAdmin/LicenseAuthority.cs`](../tools/Pinion.LicenseAdmin/LicenseAuthority.cs).

- **Token:** `base64url(payloadJson) "." base64url(signature)`
- **Signature:** **ECDSA P-256 / SHA-256** over the payload bytes (raw `r‖s`, 64 bytes).
- **Claims:** `sub` (customer/org), `ed` (edition, e.g. `pro`), `exp`, `iat`, `mid` (machine binding; `null` = floating).
- **Verification** needs only the **public key** (embedded in the product). **Minting** needs the
  **private key** (PKCS#8, base64) — server-side only.

**Key fact for this build:** ECDSA P-256 is native to Web Crypto, and verification reads the payload
bytes as-is (it does not re-serialize). So a **Supabase Edge Function (Deno) can mint licenses with no
.NET runtime** — it builds the JSON, signs it, base64url-encodes, and Pinion verifies it offline.

**Recommended policy**
- **Floating licenses (`mid = null`)** — works on the dev box *and* CI; far less support pain. The short
  expiry is the real control.
- `exp = current_period_end + ~5 days grace` — an in-flight renewal never locks anyone out.
- Offline tokens can't be revoked before expiry (by design — see the product's "security limits, not
  tamper-proof" note). Keep periods short; that *is* the revocation mechanism for refunds/chargebacks.

---

## 3. Prerequisites

### External accounts (no server work — open in parallel)
- [ ] **Paddle** (or Polar) — international cards + Merchant-of-Record VAT. Define **two prices**: monthly + annual.
- [ ] **Vipps MobilePay business** + **Recurring API** access (apply for API keys in the Vipps portal;
      recurring may require a short merchant onboarding). Define monthly + annual agreement intervals.
- [x] **MVA-registered in Norway** — required to take Vipps/card payments as seller of record for the
      Norwegian segment. (Confirmed.)
- [ ] **Resend** account (or your own SMTP) for license + receipt email.

### Server (self-hosted Supabase)
| Need | Why | How to check / get |
|---|---|---|
| **Postgres** | schema: customers / subscriptions / licenses | `docker compose ps` → `db` container |
| **Compute for functions** | webhooks + minting | `edge-runtime` container *or* a small standalone service (§5) |
| **Public HTTPS endpoint** | Vipps & Paddle must reach webhooks from the internet | domain/subdomain + reverse proxy (nginx/Caddy/Traefik) + TLS |
| **Secret storage** | the PKCS#8 signing key must live server-side only | function secret / Docker secret / env var — never in git |
| **Scheduling (cron)** | Vipps renewals — *you* trigger each charge | `pg_cron` extension or system cron hitting an endpoint |
| **Auth** | license dashboard (magic-link) | GoTrue `auth` container (already in the stack) |
| **Email** | deliver license + receipts | Resend free tier, or SMTP |
| **Supabase CLI** (dev box only) | scaffold/test functions locally | `npm i -g supabase` → `supabase --version` |

> **Most-overlooked item:** a **public HTTPS URL for webhooks.** If Supabase is only reachable internally
> today, sort that first — the providers cannot deliver subscription events to a private host.

---

## 4. Inventory your current server

Run in the Supabase install directory:

```bash
docker compose ps                                  # which services are up
docker ps --format '{{.Names}}' | grep -Ei 'edge|functions|kong|auth|rest|db'
grep -n "functions:" docker-compose.yml            # is edge-runtime defined?
# pg_cron available?
docker compose exec db psql -U postgres -c "select * from pg_available_extensions where name='pg_cron';"
```

You want: `db`, `auth`, `rest`, `kong`, and ideally a `functions`/`edge-runtime` container. On self-hosted,
functions are served through Kong at `https://<host>/functions/v1/<name>`.

---

## 5. Edge Functions vs. a standalone service

- **Edge Functions** (Deno, `edge-runtime` container): ships with the official self-hosted stack. Deploy
  by placing code in the mounted `volumes/functions/<name>/index.ts`. Convenient; recommended if present.
- **Standalone service** (Deno / Node / .NET on the same box behind your reverse proxy): functionally
  identical. Use this if your install has no `edge-runtime`, or if you'd rather reuse `LicenseAuthority`
  directly in a small **.NET** minting service. Edge Functions are **not** mandatory.

Either way, the public webhook URLs and the schema below are the same.

---

## 6. Database schema

```sql
create table customers (
  id            uuid primary key default gen_random_uuid(),
  email         text unique not null,
  name          text,
  created_at    timestamptz default now()
);

create table subscriptions (
  id                       uuid primary key default gen_random_uuid(),
  customer_id              uuid references customers(id),
  provider                 text not null,              -- 'vipps' | 'paddle'
  provider_subscription_id text not null,              -- Vipps agreementId / Paddle subscription id
  status                   text not null,              -- 'active'|'past_due'|'canceled'|'paused'
  plan                     text not null,              -- 'monthly'|'annual'
  seats                    int  not null default 1,    -- per-developer pricing
  current_period_end       timestamptz not null,
  created_at               timestamptz default now(),
  updated_at               timestamptz default now(),
  unique (provider, provider_subscription_id)
);

create table licenses (
  id              uuid primary key default gen_random_uuid(),
  subscription_id uuid references subscriptions(id),
  token           text not null,                       -- the signed ECDSA license
  subject         text not null,                       -- claims.sub
  edition         text not null,                       -- claims.ed e.g. 'pro'
  machine         text,                                -- claims.mid (null = floating)
  expires_at      timestamptz not null,
  revoked         boolean default false,
  created_at      timestamptz default now()
);

create table webhook_events (                          -- idempotency + audit
  id          uuid primary key default gen_random_uuid(),
  provider    text not null,
  event_id    text not null,
  payload     jsonb,
  received_at timestamptz default now(),
  unique (provider, event_id)                          -- dedupe duplicate deliveries
);

create table cli_tokens (                              -- CLI auth for `license refresh`
  token       text primary key,                        -- random opaque per-customer token
  customer_id uuid references customers(id),
  created_at  timestamptz default now()
);
```

**RLS:** all tables are written only by the **service role** (the functions). The dashboard reads a
customer's own rows via Supabase Auth (`auth.uid()` matched to `customers`). Never expose the service-role
key to the browser.

---

## 7. Functions / endpoints to build

| Endpoint | Rail | Does |
|---|---|---|
| `mint-license` *(internal helper)* | both | build claims → sign (ECDSA P-256) → return token |
| `vipps-checkout` | Vipps | create a Recurring **agreement**, return the approval redirect |
| `vipps-callback` | Vipps | on `ACTIVE`: upsert sub, first charge, mint + store + email |
| `vipps-renew` *(cron)* | Vipps | daily: charge subs nearing `current_period_end` (≥2 days ahead), extend + re-mint |
| `paddle-webhook` | Paddle | verify signature; on activate/renew → upsert + extend + re-mint; on cancel → run to period end |
| `license` (GET) | both | `?key=<cli_token>` → return the current signed license while the sub is active |

Minting core (Deno sketch — emits a token Pinion verifies as-is):

```ts
async function mintLicense(c: { sub: string; ed: string; expiresAt: Date; machine?: string | null }) {
  const claims = { sub: c.sub, ed: c.ed,
    exp: c.expiresAt.toISOString(), iat: new Date().toISOString(), mid: c.machine ?? null };
  const payload = new TextEncoder().encode(JSON.stringify(claims));
  const key = await crypto.subtle.importKey("pkcs8",
    pkcs8FromB64(Deno.env.get("PINION_SIGNING_KEY")!),
    { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
  const sig = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, payload));
  return b64url(payload) + "." + b64url(sig);
}
```

---

## 8. Secrets & environment

Store as function/Docker secrets — **never in git**:

| Name | What |
|---|---|
| `PINION_SIGNING_KEY` | PKCS#8 ECDSA private key (base64). The crown jewel — its leak = forgeable licenses. |
| `SUPABASE_SERVICE_ROLE_KEY` | server-side DB writes |
| `PADDLE_API_KEY` / `PADDLE_WEBHOOK_SECRET` | Paddle calls + webhook signature verification |
| `VIPPS_CLIENT_ID` / `VIPPS_CLIENT_SECRET` / `VIPPS_SUBSCRIPTION_KEY` / `VIPPS_MSN` | Vipps API auth |
| `RESEND_API_KEY` | email |

### Generate / locate the signing key
If a keypair already exists (its public key is embedded in the shipped product), put the **private** half
into `PINION_SIGNING_KEY`. If not, generate one with the admin tool and embed the public key in the product:

```
dotnet run --project tools/Pinion.LicenseAdmin -- keygen     # prints public + private (PKCS#8 base64)
```

---

## 9. Build order (each step is independently testable)

1. **Schema + `mint-license` + signing key as a secret.**
   Verify end-to-end with the product, no payments involved:
   ```
   # mint a 30-day token via the function (or LicenseAdmin), then:
   pinion license verify --license <token>      # must report valid
   ```
2. **Paddle rail** (international) — least work; Paddle manages recurring + dunning + VAT. Global card sales first.
3. **Vipps rail** (Norway) — agreement creation + the `vipps-renew` cron + re-mint.
4. **Dashboard** (Supabase Auth magic-link) + the `license` refresh endpoint + `pinion license refresh`.
5. **Site** — two-button checkout (`Pay with Vipps` / `Pay with card`), `/checkout/success`, `/account`.

---

## 10. Lifecycle & edge cases

- **Webhook idempotency:** dedupe on `(provider, event_id)` via `webhook_events` — providers retry deliveries.
- **Webhook auth:** verify Paddle's signature and Vipps' callback authenticity before acting.
- **Failed renewal:** mark `past_due`, retry per provider rules, then `canceled`; stop re-minting — the
  current token simply expires.
- **Cancellation:** license valid until `current_period_end`, then expires naturally (no re-mint).
- **Refund / chargeback:** mark `revoked`; the token still works until `exp` (short periods bound the
  exposure — that is the trade-off of offline licenses).
- **Seats (per-developer):** track `seats`; with floating licenses this is honor-system unless you bind —
  fine for launch.
- **Vipps charges are not automatic:** you create each charge ≥2 days before the due date. The cron job
  is load-bearing — monitor it.

---

## 11. Security checklist

- [ ] `PINION_SIGNING_KEY` in a secret store, not git; access limited to the minting function.
- [ ] Service-role key never reaches the browser; dashboard uses anon key + RLS.
- [ ] Webhook signature verification on **both** rails.
- [ ] HTTPS only; webhook endpoints reject unsigned/replayed events.
- [ ] License refresh is **license-only** (no source code) — preserves Pinion's trust story; document it
      in [`TRUST.md`](../TRUST.md) when shipped.
- [ ] Short token lifetimes (≤ period + small grace) so a leaked/refunded license self-expires.

---

## 12. Decisions still open

- Backend home: **Edge Functions** vs **standalone service** (depends on §4 inventory).
- Whether the signing keypair already exists or must be generated (§8).
- Edition string(s) in `claims.ed` (default `pro`).
- Email provider: Resend vs own SMTP.
