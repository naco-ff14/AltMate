# AltMate Sync Agent

An opt-in Windows companion that reads AltMate's existing shared-config.json. It never controls the game.
The plugin now uses the same exporter/transport directly from Settings → Web dashboard sync.
It adopts this agent's existing protected device identity and queue. Close the standalone watch
window before enabling the plugin; only one process can own sync on this Windows account.
The web dashboard/server have been deployed and a standalone upload was acknowledged in the setup session.
Plugin UI and plugin-originated live uploads still require in-game verification.
See [the setup guide](../docs/sync-setup-ja.txt).

## Commands

Use the packaged Windows x64 binary (self-contained .NET 10), or `dotnet run --project AltMate.SyncAgent -- <command>`.

- No arguments: help only, no upload.
- `preview`: local DPAPI-encrypted snapshot only, no network.
- `pair https://your-project.supabase.co`: prompts for a hidden, one-time dashboard code; no account password or API key.
- `sync`: explicit one-time upload.
- `watch`: foreground five-minute loop, retries temporary failures with bounded backoff; Ctrl+C stops it.
- `status`: local pairing/queue status, without credentials.
- `forget`: removes local identity, queue, acknowledgement and preview. Revoke the server device separately in the dashboard first.

No service, scheduled task or startup entry is installed. A file lock prevents concurrent processes and is released by Windows after a crash.
Never send an old web-sync-preview.json file manually. The old plaintext prototype is no longer used.

## Privacy and storage

Input: `%LOCALAPPDATA%\AltMate\shared-config.json` (read-only).
Protected state: `%LOCALAPPDATA%\AltMate\web-sync\*.bin`, current-user Windows DPAPI with atomic replacement and flushed writes.
Credentials and pending snapshots cannot be moved to another Windows account. Windows account compromise remains outside this protection.

Only explicit scalar allowlists are exported: character gil, housing, lottery, custom deliveries,
FC metadata, confirmed FC gil, confirmed FC credits, and submarine return observations.
Content/FC/retainer/house IDs, LocalLinkKey, movement and combat settings are not exported.
Entity keys use HMAC-SHA256 with a per-owner random key delivered once during pairing.
Retainer balances and treasure histories are not included. Removed source rows do not imply cloud deletion.

FC gil, credits and submarines use their own source timestamps and separate entity types. Missing or unconfirmed observations do not become zero.
Invalid/sentinel source times are skipped. The transport validates the HTTPS hosted Supabase origin and never follows redirects.

## Delivery semantics

One bounded pending snapshot (at most 1,000 entities / 1 MiB) is encrypted and flushed before the first upload.
Uncertain deliveries retry the same EventId and unchanged payload. Acknowledgements are validated before deleting the queue.
Changes accumulated while offline are coalesced from the current file on the cycle following the pending acknowledgement.
Unchanged observations are not resent. Queue exhaustion/oversized snapshots stop explicitly; data is never silently truncated.
Distinct uploads are server limited to one every five minutes per device; exact retries remain safe.
Permanent 4xx failures stop watch and retain the queue; revoke/re-pair after a rejected device credential.

Source: [feature/web-dashboard](https://github.com/naco-ff14/AltMate/tree/feature/web-dashboard), AGPL-3.0-or-later.

## Tests and packaging

```powershell
dotnet run --project tests/SyncAgent/AltMate.SyncAgent.Tests.csproj -c Release
cd web
npm ci
npm test
npm run build
npm run test:e2e
```

Agent checks cover DPAPI, allowlist/HMAC, source clocks, pairing, uncertain delivery, exact retries, revocation and invalid receipts.
Web tests include the actual PostgreSQL migrations in PGlite with pgcrypto, two-tenant RLS/privilege checks, transactional rollback, stale observations and endpoint validation.
These tests do not replace live Supabase acceptance and a real first upload.

Package from the repo root after installing web dependencies:

```powershell
./scripts/package-sync.ps1 -OutputDirectory <absolute-output-directory> -ProjectUrl https://your-project.supabase.co
```

The package contains the binary, five click-to-run command files, migration SQL, a single-file dashboard-ready Edge Function, instructions and license.
