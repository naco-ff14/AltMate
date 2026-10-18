# FC credit capture: v1.62.13 behavior and known limitations

Base: `afac14348f94189123cd19373bcc114f8b903db1` (1.62.12).
The v1.62.13 change has automated build and regression coverage. In-game native
validation has not been performed; the limitations below remain unresolved.

## Changes

- Read only `FreeCompany` text node **ID 17**, never the largest number in the UI.
- Parse whole nonnegative integers, including zero and small balances. Support
  comma, period, ordinary space, NBSP and narrow-NBSP grouping. Reject signs,
  malformed grouping, other text and overflow.
- Retain `FreeCompanyCreditShop` slot 3, accepting only UInt or a nonnegative Int.
  A negative signed value is unavailable; it is never clamped to zero.
- Queue the main window's observations from `PostRequestedUpdate`. Queue shop
  observations from the incoming `PostSetup`/`PostRefresh` AtkValue payload.
  Ignore events when the native original was prevented.
- Confirm the same value on the next scheduled check, only while the same addon,
  FC proxy, character, FC and territory remain valid. Close, setup, logout,
  invalid data and identity transitions discard pending observations.
- Keep the time of the data event, rather than the confirmation poll. Repeated
  identical events/polls do not advance it. Per-source duplicate suppression
  survives close/reopen/logout, separately for each character/FC pair.
- If this same source witnesses an intervening value in a native event, a later
  event returning to its earlier value is a new candidate, even if the intervening
  value was not confirmed before the next event. A different window or shared-file
  change does not count as witnessing that transition in this source.
- Never replace a newer or equally timestamped confirmed shared value with an
  older pending observation. An unchanged value does not renew its saved time.
- Preserve existing FC chest, submarine and treasure-voyage code and the shared
  file format. Credit timestamps remain independent of those fields.

## Native assumptions that still require in-game verification

The public structs expose no credit-specific populated/complete flag.
`IsReady` means addon setup ran; it does not mean the server supplied credits.
`PostRequestedUpdate` is associated with array-data processing and the Dalamud
documentation explicitly uses Free Company server updates as an example, but a
generic/forced array update does not prove that this particular text node changed.
Similarly, the shop's incoming setup/refresh payload must be traced to establish
that its slot 3 is the current balance on every supported path.

Consequently, the event gate plus a matching read is **not proof that an initial
zero is populated**. The candidate can accept zero under those gates, including a
placeholder zero if the native mapping/event assumptions are wrong. This must be
investigated with a live trace. No number-array offsets or request
completion hooks are guessed here, and the pure regression tests do not validate
native readiness.

Identity checks compare `InfoModule.LocalContentId`, the current player's home
world, the FC agent's proxy, the main window's agent/addon ID, and a populated
member-proxy FC ID when available. The exchange also checks its own agent's addon
ID through `AgentId.FreeCompanyCreditShop`. The public structs do not prove the global FC
proxy is always the player's own FC during inspection; test this explicitly.
Unknown/inconsistent identity preserves the saved balance. Loading the plugin
with a window already open may require closing and reopening it to get an event.
Opening the exchange without first opening the FC window must also be tested;
uninitialized FC-agent/proxy information will fail closed.

There is also a conservative missed-update case: source A last saw 100, source B
or another client saves 200, and A next receives 100 without ever witnessing the
intermediate 200. That might be a genuine return to 100 or an unchanged stale UI
receiving an unrelated event. The candidate suppresses it rather than risking
overwriting 200. This also occurs across close/reopen. A validated credit-specific
freshness signal is needed to distinguish these cases; the regression suite labels
this explicitly as a known limitation, not a solved requirement.

## Testing without risking the user's saved balances

There is **no dry-run/diagnostic mode** in this patch. Loading it can automatically
write normal plugin/shared configuration, including a false initial zero if the
native assumptions above fail. Do not test against the only copy of real data.

Before an authorized in-game test, stop **all** AltMate instances that share the
same configuration. Back up the complete `%LOCALAPPDATA%/AltMate` directory
(including `shared-config.json`) and the installation's actual Dalamud AltMate
plugin configuration using its configured plugin-config location. Keep the backup
outside those live directories; the automatic `.bak` file rotates during saves
and is not an adequate test backup. Use an isolated test profile/data directory
if available and verified. Merely using another Dalamud profile does not isolate
the hard-coded `%LOCALAPPDATA%/AltMate` shared store.

The safest first native check is a debugger/log-only observation on the existing
version, or a separately reviewed diagnostic build which skips credit writes;
neither was run or provided here. If testing the writing candidate, compare the
saved files with the backup after each scenario, and restore only while every
AltMate instance is stopped. Backups contain private configuration; keep them
local and do not upload them in reports. Pure regression tests are safe to run:
they use uniquely named temporary directories and do not load game settings.

## In-game validation checklist

Run with debug traces or a debugger that records event type, source, ready/visible
state, character/FC/addon identity, payload type/value, node 17 text and whether
the pending sample was confirmed. Do not log unrelated account data.
The patch only logs exceptions; adding trace logging or setting debugger
breakpoints at `OnCompanyCreditsUpdated`, `OnCreditShopUpdated`,
`CompanyCreditObservation.ObserveUpdate` and `CompanyCreditSample.TryApply` is a
separate test setup step. Watch the initial placeholder and the first native
response separately, rather than observing only the final displayed number.

1. Cold login/open with delayed server data and a saved nonzero balance. Confirm
   that setup placeholders never overwrite it. Repeat with an actually empty FC.
2. In both windows, verify nonzero -> zero -> nonzero; include balances matching
   rank/member counts and values 1, 9, 30, 999 and grouped numbers in supported
   client languages. Compare directly with the displayed game balance.
3. Check spending, purchases, refunds if applicable, fast close immediately after
   spending, reopening and switching tabs. Establish which events carry slot 3.
4. Invalid/missing/hidden nodes, negative Int, absent arrays, suppressed originals,
   closing, zoning, logout and character/FC switching must preserve saved values
   and must not confirm a previous session's pending sample.
5. Have a second client save a newer zero while the first window remains open.
   Poll/reopen the unchanged older window and verify zero and its timestamp stay.
   Also verify a genuinely changed later balance still syncs in both directions.
   Include 100 -> another source's 200 -> 100 with and without the original source
   actually observing the intermediate 200; record the known suppression case.
6. Inspect another FC/player and verify no balance is attributed to the wrong FC.
7. Verify FC chest withdrawal/deposit including zero, delayed chest close updates,
   submarine return times and treasure voyage records are unchanged.

If a cold placeholder is accepted, do not relax the gate or rely on more matching
polls. Identify and validate a credit-specific load/completion signal before
enabling that path in a release.

## Reference material

- [Dalamud lifecycle event semantics](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Addon/Lifecycle/AddonEvent.cs)
- [RequestedUpdate arguments](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Addon/Lifecycle/AddonArgTypes/AddonRequestedUpdateArgs.cs)
- [AtkUnitBase readiness](https://github.com/aers/FFXIVClientStructs/blob/d6397a76b3a63c2b247025dc296653182b718ca6/FFXIVClientStructs/FFXIV/Component/GUI/AtkUnitBase.cs)
- [FC proxy](https://github.com/aers/FFXIVClientStructs/blob/d6397a76b3a63c2b247025dc296653182b718ca6/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyFreeCompany.cs)
- [FC agent](https://github.com/aers/FFXIVClientStructs/blob/d6397a76b3a63c2b247025dc296653182b718ca6/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentFreeCompany.cs)

Node 17 was independently mapped from the previously reviewed executable example
in ADS. No ADS function body was copied. The exchange slot retains AltMate's
existing mapping; its event-payload mapping remains an explicit runtime check.
