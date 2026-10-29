import type { SupabaseClient } from "@supabase/supabase-js";

export type Entity = {
  entity_type: string;
  entity_key: string;
  payload: Record<string, unknown>;
  observed_at: string;
};
export type Device = {
  id: string;
  name: string;
  last_seen_at: string | null;
  revoked_at: string | null;
};
export type Snapshot = { entities: Entity[]; devices: Device[] };

// Explicit columns: never fetch device token hashes or complete configuration objects.
export async function loadSnapshot(
  client: SupabaseClient,
  userId: string,
  signal: AbortSignal,
): Promise<Snapshot> {
  const entities: Entity[] = [];
  // PostgREST defaults to 1,000 rows. Paginate without silently dropping records.
  for (let from = 0; ; from += 500) {
    const result = await client
      .from("entities")
      .select("entity_type,entity_key,payload,observed_at")
      .eq("user_id", userId)
      .order("entity_type")
      .order("entity_key")
      .range(from, from + 499)
      .abortSignal(signal);
    if (result.error)
      throw new Error(
        "同期データを取得できませんでした。接続状況とデータベースの設定を確認してください。",
      );
    const rows = result.data as Entity[];
    if (
      rows.some(
        (row) =>
          !row ||
          typeof row.entity_type !== "string" ||
          typeof row.entity_key !== "string" ||
          typeof row.observed_at !== "string" ||
          !row.payload ||
          typeof row.payload !== "object" ||
          Array.isArray(row.payload),
      )
    ) {
      throw new Error(
        "同期データの形式を確認できませんでした。管理者にお問い合わせください。",
      );
    }
    entities.push(...rows);
    if (result.data.length < 500) break;
  }
  const result = await client
    .from("devices")
    .select("id,name,last_seen_at,revoked_at")
    .eq("user_id", userId)
    .order("created_at", { ascending: false })
    .abortSignal(signal);
  if (result.error)
    throw new Error(
      "同期端末を取得できませんでした。接続状況とデータベースの設定を確認してください。",
    );
  return { entities, devices: result.data as Device[] };
}

export function stringField(
  data: Record<string, unknown>,
  key: string,
  fallback = "未記録",
): string {
  return typeof data[key] === "string" && data[key]
    ? (data[key] as string)
    : fallback;
}
export function numberField(
  data: Record<string, unknown>,
  key: string,
): number | null {
  const value = data[key];
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0
    ? value
    : null;
}
export function totalGil(entities: Entity[]): number | null {
  const values = entities
    .filter((e) => e.entity_type === "character_gil")
    .map((e) => numberField(e.payload, "Gil"))
    .filter((v) => v !== null);
  const sum = values.reduce((total, n) => total + n, 0);
  return values.length && Number.isSafeInteger(sum) ? sum : null;
}
export function dateLabel(value: unknown): string {
  if (typeof value !== "string" || !value || value.startsWith("0001-"))
    return "未記録";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "未記録" : date.toLocaleString("ja-JP");
}
export function isStale(value: string, now = Date.now()): boolean {
  const stamp = Date.parse(value);
  return !Number.isFinite(stamp) || now - stamp > 24 * 60 * 60 * 1000;
}

export function unixDateLabel(value: number | null): string {
  if (!value) return "未記録 / 待機中";
  const date = new Date(value * 1000);
  return Number.isNaN(date.getTime())
    ? "未記録"
    : dateLabel(date.toISOString());
}

// Gil and company credits have independent source clocks. Merge only for display;
// each observation remains its own row so one cannot roll back the other.
export function mergeCompanies(entities: Entity[]): Entity[] {
  const companies = new Map<string, Entity>();
  for (const entity of entities.filter((e) => e.entity_type === "free_company"))
    companies.set(entity.entity_key, {
      ...entity,
      payload: { ...entity.payload },
    });
  for (const entity of entities.filter((e) =>
    ["free_company_gil", "free_company_credits"].includes(e.entity_type),
  )) {
    const current = companies.get(entity.entity_key) ?? {
      ...entity,
      entity_type: "free_company",
      payload: {},
    };
    const fields =
      entity.entity_type === "free_company_gil"
        ? ["Gil", "GilConfirmed", "GilUpdatedAt"]
        : [
            "CompanyCredits",
            "CompanyCreditsConfirmed",
            "CompanyCreditsUpdatedAt",
          ];
    for (const field of fields)
      if (Object.hasOwn(entity.payload, field))
        current.payload[field] = entity.payload[field];
    for (const field of ["Name", "WorldName"])
      if (!current.payload[field])
        current.payload[field] = entity.payload[field];
    if (Date.parse(entity.observed_at) > Date.parse(current.observed_at))
      current.observed_at = entity.observed_at;
    companies.set(entity.entity_key, current);
  }
  return [...companies.values()];
}
