import { describe, it, expect } from "vitest";
import type { Entity } from "./data";
import {
  activeEntities,
  assetSummary,
  characters,
  personalGil,
  deliveryStatus,
  houseDeadline,
  estateRows,
  lotteryCycle,
  lotteryStatus,
  remaining,
  submarineStatus,
  weeklyReset,
  DAY,
} from "./operations";
const now = Date.parse("2026-10-29T10:00:00Z");
const row = (
  type: string,
  key: string,
  payload: Record<string, unknown>,
  stamp = now,
): Entity => ({
  entity_type: type,
  entity_key: key,
  payload,
  observed_at: new Date(stamp).toISOString(),
});
const person = { CharacterName: "A", WorldName: "W" };
const meta = (extra: Record<string, unknown> = {}) =>
  row("sync_metadata", "meta", {
    DataVersion: 2,
    CycleAnchorUtc: "2026-10-23T10:00:00Z",
    ...extra,
  });
describe("operational dashboard", () => {
  it("combines membership from more than one paired PC instead of hiding another PC", () => {
    const data = [
      meta({ WalletKeys: ["a"] }),
      { ...meta({ WalletKeys: ["b"] }), entity_key: "other-device" },
      row("character_gil", "a", { Gil: 10 }),
      row("character_gil", "b", { Gil: 20 }),
    ];
    expect(
      activeEntities(data).filter((e) => e.entity_type === "character_gil"),
    ).toHaveLength(2);
  });
  it("groups the same FC house and uses the latest entry from all members", () => {
    const address = {
      IsOwned: true,
      EstateKind: 1,
      HouseWorldId: 1,
      HouseTerritoryTypeId: 339,
      HouseWard: 2,
      HousePlot: 3,
    };
    const rows = estateRows([
      row("housing_demolition", "a", {
        ...address,
        CharacterName: "A",
        LastEnteredAt: new Date(now - 44 * DAY).toISOString(),
      }),
      row("housing_demolition", "b", {
        ...address,
        CharacterName: "B",
        LastEnteredAt: new Date(now - DAY).toISOString(),
      }),
    ]);
    expect(rows).toHaveLength(1);
    expect(houseDeadline(rows[0])).toBe(now + 44 * DAY);
  });
  it("keeps a registered character's unobserved personal balance unknown", () => {
    const person = characters([
      row("character_profile", "unknown", {
        CharacterName: "Unknown",
        WorldName: "W",
      }),
    ])[0];
    expect(personalGil(person, true)).toBeNull();
  });
  it("matches plugin assets including retainers, FC and current unchecked deposits exactly once", () => {
    const data = [
      meta(),
      row("character_gil", "a", { ...person, Gil: 100 }),
      row("retainer_gil", "r", { ...person, CharacterKey: "a", Gil: 200 }),
      row("free_company", "fc", { Name: "FC" }),
      row("free_company_gil", "fc", { Gil: 300, GilConfirmed: true }),
      row("housing_lottery", "lot", {
        ...person,
        CharacterKey: "a",
        EntryPhaseEndsAt: "2026-10-28T10:00:00Z",
        ResultChecked: false,
        BidGilDeposited: 50,
      }),
    ];
    expect(assetSummary(data, now)).toMatchObject({
      cash: 100,
      retainers: 200,
      personal: 300,
      fc: 300,
      deposit: 50,
      total: 650,
      complete: true,
    });
    data.at(-1)!.payload.ResultChecked = true;
    expect(assetSummary(data, now).total).toBe(600);
    data.at(-1)!.payload.ResultChecked = false;
    expect(assetSummary(data, now + 9 * DAY).deposit).toBe(0);
  });
  it("does not pretend old clients supplied retainer balances or unknown FC funds", () => {
    const result = assetSummary(
      [
        row("character_gil", "a", { ...person, Gil: 0 }),
        row("free_company", "fc", { Gil: 999, GilConfirmed: false }),
      ],
      now,
    );
    expect(result).toMatchObject({
      cash: 0,
      retainers: null,
      fc: null,
      total: 0,
      complete: false,
      partial: true,
    });
  });
  it("uses source membership snapshots to remove stale wallets, plots and submarine slots", () => {
    const data = [
      meta({ WalletKeys: ["a"], OpenPlotKeys: [], CompanyKeys: ["fc"] }),
      row("character_gil", "a", { Gil: 10 }),
      row("character_gil", "old", { Gil: 999 }),
      row("open_plot", "old-plot", {}),
      row("submarine_fleet", "fc", { SubmarineKeys: ["s1"] }),
      row("submarine", "s1", { FreeCompanyKey: "fc" }),
      row("submarine", "s2", { FreeCompanyKey: "fc" }),
    ];
    const result = activeEntities(data);
    expect(result.map((e) => e.entity_key)).not.toContain("old");
    expect(result.map((e) => e.entity_key)).not.toContain("old-plot");
    expect(result.map((e) => e.entity_key)).not.toContain("s2");
  });
  it("distinguishes results unchecked, no entry record, and this cycle unverified", () => {
    const end = "2026-10-28T10:00:00Z";
    const data = [
      meta(),
      row("character_profile", "a", person),
      row("character_profile", "b", { ...person, CharacterName: "B" }),
      row("character_profile", "c", { ...person, CharacterName: "C" }),
      row("housing_lottery", "l1", {
        ...person,
        CharacterKey: "a",
        EntryPhaseEndsAt: end,
        ResultChecked: false,
      }),
      row("housing_lottery", "l2", {
        ...person,
        CharacterName: "B",
        CharacterKey: "b",
        EntryPhaseEndsAt: null,
      }),
    ];
    const cycle = lotteryCycle(data, now);
    const list = characters(data);
    expect(list.map((c) => lotteryStatus(c, cycle))).toEqual([
      "結果未確認",
      "応募記録なし",
      "今期未確認",
    ]);
    expect(lotteryStatus(list[0], lotteryCycle(data, now - 2 * DAY))).toBe(
      "応募済み",
    );
  });
  it("advances house and submarine timers without a new cloud observation", () => {
    const house = row("housing_demolition", "h", {
      IsOwned: true,
      LastEnteredAt: new Date(now - 44 * DAY).toISOString(),
    });
    expect(houseDeadline(house)).toBe(now + DAY);
    expect(remaining(houseDeadline(house), now)).toBe("残り1日0時間");
    expect(remaining(houseDeadline(house), now + 2 * DAY)).toBe("期限経過");
    const ship = row("submarine", "s", {
      ReturnTimeUnix: (now + 60000) / 1000,
    });
    expect(submarineStatus(ship, now)).toBe("航海中");
    expect(submarineStatus(ship, now + 120000)).toBe("帰港予定経過");
    expect(
      submarineStatus(row("submarine", "waiting", { ReturnTimeUnix: 0 }), now),
    ).toBe("未出航・待機");
  });
  it("invalidates last week completed allowances at the exact UTC Tuesday boundary", () => {
    const reset = Date.parse("2026-10-27T08:00:00Z");
    expect(weeklyReset(reset)).toBe(reset);
    expect(weeklyReset(reset - 1)).toBe(reset - 7 * DAY);
    const list = characters([
      row("character_gil", "a", person),
      row(
        "custom_delivery",
        "d",
        { ...person, RemainingWeeklyAllowances: 0 },
        reset - 1,
      ),
    ]);
    expect(deliveryStatus(list[0], reset - 1)).toBe("消化済み");
    expect(deliveryStatus(list[0], reset)).toBe("今週未確認");
    list[0].delivery!.observed_at = new Date(reset).toISOString();
    list[0].delivery!.payload.RemainingWeeklyAllowances = 3;
    expect(deliveryStatus(list[0], reset)).toBe("未消化");
  });
});
