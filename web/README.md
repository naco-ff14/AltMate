# AltMate Web Dashboard

Private, read-only React + TypeScript + Vite application for the existing Supabase schema.

## Local development

Use Node.js 24 LTS (minimum 22.12). Inside `web`:

```sh
npm ci
cp .env.example .env.local
# Set the project URL and PUBLIC publishable/legacy anon key in .env.local.
npm run dev
npm test
npm run build
npx playwright install chromium
npm run test:e2e
```

No production credentials are required for tests. Browser tests mock Supabase HTTP responses;
they exercise the real auth client and UI, but do not prove live database tenant isolation.
The lockfile is committed; generated files and local environment files are ignored.

## Cloudflare Pages: before Save and Deploy

| Setting                                 | Value                                               |
| --------------------------------------- | --------------------------------------------------- |
| Repository                              | `naco-ff14/AltMate`                                 |
| Production branch                       | `feature/web-dashboard`                             |
| Framework                               | React (Vite)                                        |
| Root directory                          | `web`                                               |
| Build command                           | `npm run build`                                     |
| Build output directory                  | `dist`                                              |
| Build variable `NODE_VERSION`           | `24`                                                |
| Build variable `VITE_SUPABASE_URL`      | Your Tokyo project's HTTPS project URL              |
| Build variable `VITE_SUPABASE_ANON_KEY` | Public `sb_publishable_…` key, or legacy `anon` JWT |

Set the two Vite variables in the **Production build environment before the first build**.
Changing them requires a rebuild. Preview deployments need their own environment variables if used.
Both values are intentionally visible in the browser. Never enter `sb_secret_…`, a `service_role`
JWT, database password, access token, or account password into a `VITE_` variable.
The build rejects non-public keys before creating the bundle. Missing variables build a setup
message; they do not produce a functioning login screen.

In Supabase:

1. Verify `202610280001_web_dashboard.sql` has been applied. Do not re-run CREATE TABLE statements
   on an existing database. All four tables must have RLS enabled. `entities`, `devices`, and
   `entity_history` must permit SELECT only where `user_id = auth.uid()`; no public read policy
   or client write policy should be present. `sync_events` has no client read policy.
2. Email/password authentication must be enabled. Keep public signup and anonymous login disabled.
3. Create your login user under **Authentication → Users → Add user → Create new user** with
   an email and password. Confirm the email using the dashboard option if available. This app
   deliberately has no public signup flow.
4. After deployment, set Supabase's Site URL to the actual HTTPS Pages URL. Password sign-in itself
   does not need a callback route. For password recovery, set **Authentication → URL Configuration**:
   Site URL `https://altmate-web.pages.dev` and Redirect URLs `https://altmate-web.pages.dev/?reset=password`.
   Use the actual production origin if the domain changes. The login screen's "パスワードを忘れた方"
   sends recovery email, and verified recovery links show a new-password form. Dashboard-generated
   recovery email also works when Site URL points here. Expired links can be re-requested.
   Recovery tokens are consumed by the auth SDK; never share recovery URLs or passwords in chat.
5. Verify with two real test users that each sees only their own records, and a logged-out request
   cannot read them. A filter in the browser is defense in depth; RLS is the security boundary.

The existing Cloudflare settings can then be saved and deployed. Verify the deployment's commit,
build status, HTTPS login screen, login/logout, mobile layout, and security response headers.
Cloudflare serves `public/_headers` with the generated site. The CSP supports hosted
`*.supabase.co` projects; a future custom Supabase domain needs an explicit CSP/config change.

## Current scope and data

Implemented: login/logout and session restoration, owner-filtered paginated reads, search,
manual refresh, errors with retry, last observation times, old-observation flags, responsive
overview/characters/housing/gil/FC/submarines/custom-delivery/device screens, and honest empty states.

The website reads `entities(entity_type, entity_key, payload, observed_at)` and safe columns from
`devices`. Payloads currently follow the PascalCase fields in the offline exporter:

| `entity_type`                       | Payload fields used                                                                                                  |
| ----------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `character_gil`                     | `CharacterName`, `WorldName`, `Gil`                                                                                  |
| `housing_demolition`                | `CharacterName`, `WorldName`, `EstateKind` (0 personal / 1 FC), `IsOwned`, `HouseWard`, `HousePlot`, `LastEnteredAt` |
| `free_company`                      | `Name`, `WorldName`, `Gil`, `GilConfirmed`, `CompanyCredits`, `CompanyCreditsConfirmed`, `CompanyCreditsUpdatedAt`   |
| `custom_delivery`                   | `CharacterName`, `WorldName`, `RemainingWeeklyAllowances`, purple/orange crafter/gatherer scrip fields               |
| `submarine` (future producer)       | `Name`, `FreeCompanyName`, `WorldName`, `ReturnTimeUnix`                                                             |
| `housing_lottery` (future producer) | `CharacterName`, `WorldName`, `PlotAddress`, `EntryPhaseEndsAt`, `ResultChecked`                                     |

Submarine and lottery screens can render these records, but the current exporter does not produce
them. FC gil fields also are not exported yet; missing/unconfirmed balances remain unknown.
Character gil totals exclude retainers and FC balances. Housing addresses are displayed as the
stored one-based ward/plot values. No automatic demolition deadline is claimed.

**Not implemented or deployed:** authenticated upload endpoint, secure one-time pairing, Windows
DPAPI device token storage, account-scoped HMAC entity keys, durable upload queue, stale-update
merge/idempotency, or game-to-cloud end-to-end sync. The agent remains an offline preview;
do not upload its JSON manually. No PWA/service worker caches private data.

The next sync milestone must cover those requirements and live two-tenant/RLS tests before
enabling any upload. No database/service credential belongs in the web app or Windows agent.

Official references: [Cloudflare build configuration](https://developers.cloudflare.com/pages/configuration/build-configuration/),
[Supabase API keys](https://supabase.com/docs/guides/getting-started/api-keys),
[Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security).
