# AltMate Sync Agent — local preview

This is a first-stage offline exporter, **not** a working cloud sync agent.

Run `dotnet run --project AltMate.SyncAgent` on Windows with an existing AltMate shared-config.json.
It creates `%LOCALAPPDATA%\\AltMate\\web-sync-preview.json`.
No HTTP requests are made. Do not upload this preview file: it includes private game/account information.

The exporter uses an explicit allowlist and excludes LocalLinkKey, raw ContentId fields, movement settings and combat settings. Keys are SHA-256 pseudonyms, **not anonymization**; future versions must use account-scoped HMAC before sending data.

Next work: per-field source timestamp rules, submarine/lottery DTOs, DPAPI-protected device identity, durable offline queue, pairing, authenticated Edge Function, and tests. Never enable cloud upload by default.
