type Rule = "text" | "uint" | "bool" | "date" | "nullableDate";
const schemas: Record<string, Record<string, Rule>> = {
  character_gil: { CharacterName: "text", WorldName: "text", Gil: "uint" },
  custom_delivery: {
    CharacterName: "text",
    WorldName: "text",
    RemainingWeeklyAllowances: "uint",
    PurpleCrafterScrip: "uint",
    PurpleGathererScrip: "uint",
    OrangeCrafterScrip: "uint",
    OrangeGathererScrip: "uint",
  },
  housing_demolition: {
    CharacterName: "text",
    WorldName: "text",
    EstateKind: "uint",
    IsOwned: "bool",
    HouseWorldId: "uint",
    HouseTerritoryTypeId: "uint",
    HouseWard: "uint",
    HousePlot: "uint",
    LastEnteredAt: "nullableDate",
    LastOwnershipCheckedAt: "date",
  },
  housing_lottery: {
    CharacterName: "text",
    WorldName: "text",
    PlotAddress: "text",
    EntryPhaseEndsAt: "nullableDate",
    ResultChecked: "bool",
    BidGilDeposited: "uint",
  },
  free_company: { Name: "text", WorldName: "text" },
  free_company_gil: {
    Name: "text",
    WorldName: "text",
    Gil: "uint",
    GilConfirmed: "bool",
    GilUpdatedAt: "date",
  },
  free_company_credits: {
    Name: "text",
    WorldName: "text",
    CompanyCredits: "uint",
    CompanyCreditsConfirmed: "bool",
    CompanyCreditsUpdatedAt: "date",
  },
  submarine: {
    Name: "text",
    FreeCompanyName: "text",
    WorldName: "text",
    ReturnTimeUnix: "uint",
  },
};
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const hex = /^[a-f0-9]{64}$/;
function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
function date(value: unknown): boolean {
  return (
    typeof value === "string" &&
    value.length <= 40 &&
    /(?:Z|[+-]\d\d:\d\d)$/.test(value) &&
    Number.isFinite(Date.parse(value)) &&
    Date.parse(value) >= Date.parse("2000-01-01Z")
  );
}
function uint(value: unknown): boolean {
  return (
    typeof value === "number" &&
    Number.isSafeInteger(value) &&
    value >= 0 &&
    value <= 4294967295
  );
}
export function validateEnvelope(value: unknown, now = Date.now()): boolean {
  if (
    !object(value) ||
    Object.keys(value).some(
      (k) =>
        ![
          "SchemaVersion",
          "DeviceId",
          "Revision",
          "EventId",
          "CapturedAt",
          "Changes",
        ].includes(k),
    ) ||
    value.SchemaVersion !== 1 ||
    typeof value.DeviceId !== "string" ||
    !uuid.test(value.DeviceId) ||
    typeof value.EventId !== "string" ||
    !uuid.test(value.EventId) ||
    typeof value.Revision !== "number" ||
    !Number.isSafeInteger(value.Revision) ||
    value.Revision < 0 ||
    !date(value.CapturedAt) ||
    Date.parse(value.CapturedAt as string) > now + 300000 ||
    !Array.isArray(value.Changes) ||
    value.Changes.length < 1 ||
    value.Changes.length > 1000
  )
    return false;
  const seen = new Set<string>();
  for (const change of value.Changes) {
    if (
      !object(change) ||
      Object.keys(change).some(
        (k) => !["EntityType", "EntityKey", "ObservedAt", "Data"].includes(k),
      ) ||
      typeof change.EntityType !== "string" ||
      !Object.hasOwn(schemas, change.EntityType) ||
      typeof change.EntityKey !== "string" ||
      !hex.test(change.EntityKey) ||
      !date(change.ObservedAt) ||
      Date.parse(change.ObservedAt as string) > now + 300000 ||
      !object(change.Data)
    )
      return false;
    const key = `${change.EntityType}:${change.EntityKey}`;
    if (seen.has(key)) return false;
    seen.add(key);
    const schema = schemas[change.EntityType];
    if (Object.keys(change.Data).length === 0) return false;
    for (const [field, item] of Object.entries(change.Data)) {
      if (!Object.hasOwn(schema, field)) return false;
      switch (schema[field]) {
        case "text":
          if (
            typeof item !== "string" ||
            item.length > 256 ||
            /[\u0000-\u001f]/.test(item)
          )
            return false;
          break;
        case "uint":
          if (!uint(item)) return false;
          break;
        case "bool":
          if (typeof item !== "boolean") return false;
          break;
        case "date":
          if (!date(item)) return false;
          break;
        case "nullableDate":
          if (item !== null && !date(item)) return false;
          break;
      }
    }
    if (
      change.EntityType === "free_company_gil" &&
      (change.Data.GilConfirmed !== true ||
        !uint(change.Data.Gil) ||
        !date(change.Data.GilUpdatedAt))
    )
      return false;
    if (
      change.EntityType === "free_company_credits" &&
      (change.Data.CompanyCreditsConfirmed !== true ||
        !uint(change.Data.CompanyCredits) ||
        !date(change.Data.CompanyCreditsUpdatedAt))
    )
      return false;
  }
  return true;
}

type Rpc = (
  name: string,
  args: Record<string, unknown>,
) => PromiseLike<{
  data: unknown;
  error: { code?: string; message?: string } | null;
}>;
const headers = {
  "Content-Type": "application/json",
  "Cache-Control": "no-store",
};
const reply = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers });
export async function handleSync(
  request: Request,
  rpc: Rpc,
): Promise<Response> {
  // Browser clients pair through authenticated database RPCs; device transport does not enable CORS.
  if (request.method !== "POST")
    return reply(405, { error: "method_not_allowed" });
  const token = request.headers
    .get("Authorization")
    ?.match(/^Bearer ([a-f0-9]{64})$/)?.[1];
  if (!token) return reply(401, { error: "invalid_credentials" });
  if (!request.headers.get("Content-Type")?.startsWith("application/json"))
    return reply(415, { error: "json_required" });
  if (Number(request.headers.get("Content-Length")) > 1048576)
    return reply(413, { error: "body_too_large" });
  try {
    const reader = request.body?.getReader();
    if (!reader) return reply(400, { error: "invalid_body" });
    const chunks: Uint8Array[] = [];
    let size = 0;
    for (;;) {
      const result = await reader.read();
      if (result.done) break;
      size += result.value.byteLength;
      if (size > 1048576) {
        await reader.cancel();
        return reply(413, { error: "body_too_large" });
      }
      chunks.push(result.value);
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) {
      bytes.set(chunk, offset);
      offset += chunk.length;
    }
    let body: unknown;
    try {
      body = JSON.parse(
        new TextDecoder("utf-8", { fatal: true }).decode(bytes),
      );
    } catch {
      return reply(400, { error: "invalid_json" });
    }
    const pairing =
      object(body) && body.Action === "pair" && Object.keys(body).length === 1;
    if (!pairing && !validateEnvelope(body))
      return reply(400, { error: "invalid_envelope" });
    const result = pairing
      ? await rpc("claim_sync_pairing", { p_code: token })
      : await rpc("ingest_sync", { p_token: token, p_envelope: body });
    if (result.error) {
      const message = result.error.message;
      if (["invalid_pairing", "invalid_device"].includes(message ?? ""))
        return reply(401, { error: "invalid_credentials" });
      if (
        [
          "sync_rate_limited",
          "pairing_rate_limited",
          "device_limit_reached",
        ].includes(message ?? "")
      ) {
        return new Response(JSON.stringify({ error: "rate_limited" }), {
          status: 429,
          headers: { ...headers, "Retry-After": "300" },
        });
      }
      if (message === "event_conflict" || result.error.code === "23505")
        return reply(409, { error: "event_conflict" });
      if (result.error.code === "22023")
        return reply(400, { error: "invalid_envelope" });
      return reply(503, { error: "sync_unavailable" });
    }
    return reply(200, result.data);
  } catch {
    return reply(503, { error: "sync_unavailable" });
  }
}
