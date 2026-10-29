# AltMate Web Dashboard — implementation plan

Status: Web dashboard deployed. Authenticated sync backend and Windows companion implemented and locally tested; server migration/function deployment, daily retention scheduling and first live upload still pending. See `docs/sync-setup-ja.txt` and `AltMate.SyncAgent/README.md`.

## Architecture
AltMate shared-config.json -> Windows SyncAgent -> authenticated Supabase Edge Function -> PostgreSQL RLS -> Cloudflare Pages PWA. Future SwiftUI uses the same read API.

## Security
- Opt-in sync, disabled by default.
- Never upload the full Configuration object. Use explicit allowlisted DTOs.
- Exclude LocalLinkKey, raw content IDs, retainer IDs, and movement/combat controls.
- Device pairing must be short-lived, one-time and rate-limited.
- Device tokens must be stored with Windows DPAPI and only hashes held server-side.
- Do not deploy until token validation, tenant isolation, idempotency and RLS tests pass.

## Sync semantics
- Changes coalesced; minimum interval five minutes except initial/manual sync.
- Unique eventId for idempotent retries; stable entity keys per owner.
- Use source observedAt, not receipt time, for stale-update prevention.
- Preserve confirmed FC credit observations; missing values never become zero.
- Housing demolition dates are estimates and must show last observed time.
- Keep current entity state; prune history and event logs older than 90 days.
- Offline durable queue, bounded retry backoff and clear sync status.

## Implementation order
1. Shared DTOs and tests (started).
2. Allowlisted exporter and revision snapshot integration.
3. Windows SyncAgent, pairing and offline queue.
4. Edge Functions with authentication and transactional upsert.
5. Cloudflare Pages PWA and responsive dashboard.
6. End-to-end tests and deployment.
7. SwiftUI client using the same API.

## Cloud prerequisites
User-owned Supabase and Cloudflare projects are required before deployment.
