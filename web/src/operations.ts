import { mergeCompanies, numberField as num, stringField as str } from "./data";
import type { Entity } from "./data";
export const DAY = 86400000;
export function time(value: unknown): number | null {
  if (typeof value !== "string" || value.startsWith("0001-")) return null;
  const stamp = Date.parse(value);
  return Number.isFinite(stamp) ? stamp : null;
}
export function metadata(entities: Entity[]): Entity | undefined {
  return entities
    .filter((e) => e.entity_type === "sync_metadata")
    .sort((a, b) => (time(b.observed_at) ?? 0) - (time(a.observed_at) ?? 0))[0];
}
export function activeEntities(entities: Entity[]): Entity[] {
  const snapshots = entities.filter((e) => e.entity_type === "sync_metadata");
  const lists: Record<string, string> = {
    character_profile: "CharacterKeys",
    character_gil: "WalletKeys",
    retainer_gil: "RetainerKeys",
    free_company: "CompanyKeys",
    free_company_gil: "CompanyKeys",
    free_company_credits: "CompanyKeys",
    submarine_fleet: "CompanyKeys",
    open_plot: "OpenPlotKeys",
    housing_demolition: "HousingKeys",
    housing_lottery: "LotteryKeys",
    custom_delivery: "DeliveryKeys",
  };
  const allowed = entities.filter((e) => {
    const field = lists[e.entity_type];
    if (!field) return true;
    const scopes = snapshots
      .map((snapshot) => snapshot.payload[field])
      .filter(Array.isArray);
    return (
      scopes.length === 0 ||
      scopes.some((scope) => scope.includes(e.entity_key))
    );
  });
  const fleets = new Map(
    allowed
      .filter((e) => e.entity_type === "submarine_fleet")
      .map((e) => [e.entity_key, e.payload.SubmarineKeys]),
  );
  return allowed.filter(
    (e) =>
      e.entity_type !== "submarine" ||
      !fleets.has(str(e.payload, "FreeCompanyKey", "")) ||
      (fleets.get(str(e.payload, "FreeCompanyKey", "")) as unknown[]).includes(
        e.entity_key,
      ),
  );
}
export type Character = {
  key: string;
  name: string;
  world: string;
  wallet?: Entity;
  profile?: Entity;
  lottery?: Entity;
  delivery?: Entity;
  retainers: Entity[];
};
const identity = (e: Entity) =>
  `${str(e.payload, "CharacterName", "")}@${str(e.payload, "WorldName", "")}`;
export function characters(entities: Entity[]): Character[] {
  const result = new Map<string, Character>();
  const names = new Map<string, string>();
  const ensure = (e: Entity, key: string) => {
    if (!result.has(key))
      result.set(key, {
        key,
        name: str(e.payload, "CharacterName"),
        world: str(e.payload, "WorldName"),
        retainers: [],
      });
    names.set(identity(e), key);
    return result.get(key)!;
  };
  for (const e of entities.filter((e) => e.entity_type === "character_profile"))
    ensure(e, e.entity_key).profile = e;
  for (const e of entities.filter((e) => e.entity_type === "character_gil"))
    ensure(e, e.entity_key).wallet = e;
  for (const e of entities.filter((e) =>
    ["housing_lottery", "custom_delivery"].includes(e.entity_type),
  )) {
    const key =
      str(e.payload, "CharacterKey", "") ||
      names.get(identity(e)) ||
      identity(e);
    const character = ensure(e, key);
    if (e.entity_type === "housing_lottery") character.lottery = e;
    else character.delivery = e;
  }
  for (const e of entities.filter((e) => e.entity_type === "retainer_gil")) {
    const key =
      str(e.payload, "CharacterKey", "") ||
      names.get(identity(e)) ||
      identity(e);
    ensure(e, key).retainers.push(e);
  }
  return [...result.values()].sort(
    (a, b) =>
      a.name.localeCompare(b.name, "ja") ||
      a.world.localeCompare(b.world, "ja"),
  );
}
export type Cycle = {
  start: number;
  entryEnd: number;
  end: number;
  phase: "entry" | "results";
  inferred: boolean;
};
export function lotteryCycle(entities: Entity[], now: number): Cycle | null {
  let anchor = time(metadata(entities)?.payload.CycleAnchorUtc);
  const inferred = anchor === null;
  if (anchor === null) {
    const ends = entities
      .filter((e) => e.entity_type === "housing_lottery")
      .map((e) => time(e.payload.EntryPhaseEndsAt))
      .filter((v) => v !== null);
    if (ends.length) anchor = Math.max(...ends) - 5 * DAY;
  }
  if (anchor === null) return null;
  const start = anchor + Math.floor((now - anchor) / (9 * DAY)) * 9 * DAY;
  return {
    start,
    entryEnd: start + 5 * DAY,
    end: start + 9 * DAY,
    phase: now < start + 5 * DAY ? "entry" : "results",
    inferred,
  };
}
export function hasEntry(
  entity: Entity | undefined,
  cycle: Cycle | null,
): boolean {
  const end = time(entity?.payload.EntryPhaseEndsAt);
  return (
    end !== null && cycle !== null && Math.abs(end - cycle.entryEnd) < 300000
  );
}
export type LotteryStatus =
  | "応募済み"
  | "応募記録なし"
  | "今期未確認"
  | "結果未確認"
  | "結果確認済み"
  | "周期未確認";
export function lotteryStatus(
  character: Character,
  cycle: Cycle | null,
): LotteryStatus {
  if (!cycle) return "周期未確認";
  if (hasEntry(character.lottery, cycle))
    return cycle.phase === "entry"
      ? "応募済み"
      : character.lottery?.payload.ResultChecked === true
        ? "結果確認済み"
        : "結果未確認";
  return (time(character.lottery?.observed_at) ?? 0) >= cycle.start
    ? "応募記録なし"
    : "今期未確認";
}
export function sum(values: (number | null)[]): number | null {
  const known = values.filter((v) => v !== null);
  if (!known.length) return null;
  const total = known.reduce((a, b) => a + b, 0);
  return Number.isSafeInteger(total) ? total : null;
}
export function wallet(character: Character): number | null {
  return character.wallet ? num(character.wallet.payload, "Gil") : null;
}
export function retainerGil(
  character: Character,
  complete: boolean,
): number | null {
  return character.retainers.length
    ? sum(character.retainers.map((e) => num(e.payload, "Gil")))
    : complete
      ? 0
      : null;
}
export function personalGil(
  character: Character,
  complete: boolean,
): number | null {
  const cash = wallet(character);
  const retainer = retainerGil(character, complete);
  return cash === null && (retainer === null || retainer === 0)
    ? null
    : (cash ?? 0) + (retainer ?? 0);
}
export function assetSummary(entities: Entity[], now: number) {
  const complete =
    (num(metadata(entities)?.payload ?? {}, "DataVersion") ?? 0) >= 2;
  const roster = characters(entities);
  const companies = mergeCompanies(entities);
  const cycle = lotteryCycle(entities, now);
  const cash = sum(roster.map(wallet));
  const retainers = sum(roster.map((c) => retainerGil(c, complete)));
  const personal = sum([cash, retainers]);
  // Match plugin totals: wallets + retainers + FC balances + current unchecked lottery deposits.
  const fc = sum(
    companies.map((e) =>
      e.payload.GilConfirmed === true ? num(e.payload, "Gil") : null,
    ),
  );
  const deposits = entities
    .filter(
      (e) =>
        e.entity_type === "housing_lottery" &&
        hasEntry(e, cycle) &&
        e.payload.ResultChecked !== true,
    )
    .map((e) => num(e.payload, "BidGilDeposited"));
  const deposit = deposits.length ? sum(deposits) : cycle ? 0 : null;
  return {
    cash,
    retainers,
    personal,
    fc,
    deposit,
    total:
      personal !== null || fc !== null || deposits.length
        ? sum([personal, fc, deposit])
        : null,
    complete,
    partial:
      !complete ||
      roster.some((c) => wallet(c) === null) ||
      companies.some((e) => e.payload.GilConfirmed !== true) ||
      (!cycle && entities.some((e) => e.entity_type === "housing_lottery")),
  };
}
export function weeklyReset(now: number): number {
  const date = new Date(now);
  const reset = Date.UTC(
    date.getUTCFullYear(),
    date.getUTCMonth(),
    date.getUTCDate(),
    8,
  );
  let result = reset - ((date.getUTCDay() - 2 + 7) % 7) * DAY;
  if (result > now) result -= 7 * DAY;
  return result;
}
export type DeliveryStatus = "未消化" | "消化済み" | "今週未確認" | "記録なし";
export function deliveryStatus(
  character: Character,
  now: number,
): DeliveryStatus {
  if (!character.delivery) return "記録なし";
  if ((time(character.delivery.observed_at) ?? 0) < weeklyReset(now))
    return "今週未確認";
  const remaining = num(
    character.delivery.payload,
    "RemainingWeeklyAllowances",
  );
  return remaining === null
    ? "今週未確認"
    : remaining > 0
      ? "未消化"
      : "消化済み";
}
export function houseDeadline(entity: Entity): number | null {
  const entered = time(entity.payload.LastEnteredAt);
  return entity.payload.IsOwned === true && entered !== null
    ? entered + 45 * DAY
    : null;
}
export function estateRows(entities: Entity[]): Entity[] {
  const groups = new Map<string, Entity[]>();
  for (const e of entities.filter(
    (e) => e.entity_type === "housing_demolition" && e.payload.IsOwned === true,
  )) {
    const address = [
      "HouseWorldId",
      "HouseTerritoryTypeId",
      "HouseWard",
      "HousePlot",
    ].map((field) => num(e.payload, field));
    const key =
      e.payload.EstateKind === 1 &&
      address.every((value) => value !== null && value > 0)
        ? `fc:${address.join(":")}`
        : e.entity_key;
    const group = groups.get(key) ?? [];
    group.push(e);
    groups.set(key, group);
  }
  return [...groups.entries()].map(([key, group]) => {
    const owner = [...group].sort(
      (a, b) =>
        (time(b.payload.LastOwnershipCheckedAt) ?? time(b.observed_at) ?? 0) -
        (time(a.payload.LastOwnershipCheckedAt) ?? time(a.observed_at) ?? 0),
    )[0];
    const entry = [...group].sort(
      (a, b) =>
        (time(b.payload.LastEnteredAt) ?? 0) -
        (time(a.payload.LastEnteredAt) ?? 0),
    )[0];
    return {
      ...owner,
      entity_key: key,
      payload: {
        ...owner.payload,
        LastEnteredAt: entry.payload.LastEnteredAt,
        CoOwners:
          group.length > 1
            ? [
                ...new Set(group.map((e) => str(e.payload, "CharacterName"))),
              ].join(" / ")
            : "",
      },
    };
  });
}
export function submarineStatus(
  entity: Entity,
  now: number,
): "航海中" | "帰港予定経過" | "未出航・待機" | "未確認" {
  const value = num(entity.payload, "ReturnTimeUnix");
  return value === null
    ? "未確認"
    : value === 0
      ? "未出航・待機"
      : value * 1000 > now
        ? "航海中"
        : "帰港予定経過";
}
export function remaining(deadline: number | null, now: number): string {
  if (deadline === null) return "未記録";
  const diff = deadline - now;
  if (diff <= 0) return "期限経過";
  const days = Math.floor(diff / DAY);
  const hours = Math.floor((diff % DAY) / 3600000);
  const minutes = Math.floor((diff % 3600000) / 60000);
  return days
    ? `残り${days}日${hours}時間`
    : hours
      ? `残り${hours}時間${minutes}分`
      : `残り${Math.max(1, minutes)}分`;
}
export function matches(values: unknown[], query: string): boolean {
  const q = query.trim().toLocaleLowerCase();
  return (
    !q ||
    values.some(
      (v) => typeof v === "string" && v.toLocaleLowerCase().includes(q),
    )
  );
}
export function district(id: number | null): string {
  return (
    (
      {
        339: "ミスト・ヴィレッジ",
        340: "ラベンダーベッド",
        341: "ゴブレットビュート",
        641: "シロガネ",
        979: "エンピレアム",
      } as Record<number, string>
    )[id ?? 0] ?? "地区未記録"
  );
}
