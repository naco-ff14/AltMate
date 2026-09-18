# Regression checks

Run from the repository root:

```powershell
dotnet run --project tests/Regression/Regression.csproj -c Release
```

Uses the production configuration model and shared store with an isolated temporary
directory and mutex. It does not access installed game settings or require Dalamud.

Checks independent settings edits from two clients, nonblocking mutex contention,
retry persistence of gear quantities, duplicate notifications, unchanged-file
polling, and detection of external edits.

FC chest checks cover confirmed zero balances, stale-client merges, and resetting
consecutive observations when data becomes unavailable or the character/FC changes.
They also cover closing immediately after a withdrawal, accepting delayed updates
only within the same confirmed chest session, expiry, zoning and logout.

Game validation is still needed for UI interactions and performance: changing gear
quantities, switching characters, inventory changes, crafting progress/level changes,
HQ-only quest readiness, and synchronization between running clients.
