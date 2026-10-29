# AltMate Web Dashboard

Private, read-only React + TypeScript + Vite application for the existing Supabase schema.

Plugin linking: apply `202610290003_plugin_pairing.sql` after dashboard-v2 and update the
`sync` Edge Function. The plugin generates a 12-character code; sign in with email,
password and the optional code to approve in one submission. Existing sessions approve
under Settings → Plugin linking. Browser approval uses the authenticated RPC only;
credential polling requires a separate 256-bit plugin secret. Codes are never stored
in browser storage or URLs. Existing device registrations continue to work.

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
`devices`. Payloads follow the PascalCase fields in the allowlisted Sync Agent:

| `entity_type`        | Payload fields used                                                                                                  |
| -------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `character_gil`      | `CharacterName`, `WorldName`, `Gil`                                                                                  |
| `housing_demolition` | `CharacterName`, `WorldName`, `EstateKind` (0 personal / 1 FC), `IsOwned`, `HouseWard`, `HousePlot`, `LastEnteredAt` |
| `free_company`       | `Name`, `WorldName`, `Gil`, `GilConfirmed`, `CompanyCredits`, `CompanyCreditsConfirmed`, `CompanyCreditsUpdatedAt`   |
| `custom_delivery`    | `CharacterName`, `WorldName`, `RemainingWeeklyAllowances`, purple/orange crafter/gatherer scrip fields               |
| `submarine`          | `Name`, `FreeCompanyName`, `WorldName`, `ReturnTimeUnix`                                                             |
| `housing_lottery`    | `CharacterName`, `WorldName`, `PlotAddress`, `EntryPhaseEndsAt`, `ResultChecked`                                     |

The Sync Agent now produces submarine and lottery records. Confirmed FC gil and credits are
stored as `free_company_gil` and `free_company_credits` with independent observation clocks;
the dashboard merges them with `free_company` metadata for display only. Unknown balances
remain unknown. Personal totals include separately observed retainer balances when v2 data is available; total assets also include FC funds and current unchecked lottery deposits. Housing addresses are
stored one-based ward/plot values; no automatic demolition deadline is claimed.

## Authenticated sync deployment

Apply `supabase/migrations/202610290001_authenticated_sync.sql` once after the original migration.
It makes SELECT privileges explicit (the first live setup required this), limits device columns,
and adds authenticated code issuance/device revocation plus service-only pairing/upload functions.
Deploy `supabase/functions/sync` with `supabase/config.toml` (`verify_jwt = false`): this endpoint
uses independently validated random device credentials, not user JWTs. Only the server holds
its Supabase service credential. No new Vite environment variables are required.

The `同期端末` screen issues one-time 10-minute codes and revokes devices after confirmation.
Pairing issuance is rate limited and each owner can have at most ten active devices.
Source observation timestamps govern updates; duplicate event UUIDs are acknowledged only for
identical owner/device/content. See [the Japanese setup guide](../docs/sync-setup-ja.txt).

Implemented and locally tested: authenticated endpoint, pairing, account-scoped HMAC keys,
Windows DPAPI credential/queue storage, durable retries, stale observation rejection and idempotency.
**Still requires operator action/verification:** applying the SQL, publishing the Edge Function,
setting its custom-auth configuration, daily scheduling of `public.prune_sync_history()` for
90-day retention, live Supabase two-user isolation tests, PC pairing and first real upload.
No automatic cloud deletion of removed source records, treasure histories,
PWA/service worker, Windows service or startup integration is implemented.
Official references: [Cloudflare build configuration](https://developers.cloudflare.com/pages/configuration/build-configuration/),
[Supabase API keys](https://supabase.com/docs/guides/getting-started/api-keys),
[Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security).

## Dashboard v2 operational views

Overview is limited to the total asset summary and actionable housing/submarine/delivery counts.
Housing separates lottery status, estimated estate deadlines and observed open plots. Money tables
support name and ascending/descending wallet/personal-asset sorting. FC fleets group pending boats.
Deliveries show incomplete, completed, this-week-unverified and unobserved characters with scrip balances.
Timers recompute every 30 seconds and when the page regains focus; this does not imply a new game observation.
FC estate rows use the latest entry from all records for the same address.

Additional allowlisted data is implemented in the exporter: `retainer_gil`, `character_profile`,
`open_plot`, `submarine_fleet`, `sync_metadata`, and hashed `CharacterKey` / `FreeCompanyKey` associations.
Metadata snapshots are scoped per paired PC and their active lists are unioned for display. Metadata
is not added to entity history. User IDs, raw content/retainer/FC IDs and local control keys remain excluded.

Existing deployments must apply `202610290002_dashboard_views.sql`, replace the deployed `sync`
function with the current source, then use the expanded exporter. The public plugin v1.62.14 does
not produce the added rows. Until upgraded, the website works with existing records and explicitly
marks missing balances/membership as unknown. Scheduled future lottery deadlines are preserved
independently from the observation timestamp's anti-future-clock check.
See [Japanese upgrade notes](../docs/dashboard-v2-ja.txt). Database RLS and device authentication are unchanged.
